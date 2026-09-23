/* ============================================================================
 * copy-probe —— 判定「这个文件系统上，内核级加速拷贝会不会卡死」
 *
 * 为什么需要它：
 *   .NET 的 File.Copy 在 Linux 上按固定顺序尝试四条路径
 *   （见 dotnet/runtime 的 src/native/libs/System.Native/pal_io.c
 *     函数 SystemNative_CopyFile）：
 *
 *       1. ioctl(FICLONE)       —— 写时复制克隆
 *       2. copy_file_range()    —— 内核态拷贝
 *       3. sendfile()           —— 零拷贝
 *       4. read()/write()       —— 普通用户态循环（兜底）
 *
 *   前三条都依赖文件系统实现。在 overlayfs（Docker / 容器）、网络盘、
 *   以及某些虚拟文件系统上，它们存在已知缺陷：内核可能陷入死循环或
 *   相互等待，表现为
 *       watchdog: BUG: soft lockup - CPU#1 stuck for 52s! [Parallel Copy T:...]
 *   并且 kill -9 也无法终止（进程停在系统调用里，信号送不进去）。
 *
 *   本探针把四条路径逐条单独跑一遍，让「会不会卡」在十几秒内给出答案，
 *   而不是等 MSBuild 的 Copy 任务卡上半小时。
 *
 * 用法：
 *   copy-probe <ficlone|cfr|sendfile|rw> <源文件> <目标文件>
 *
 * 输出（stdout，单行，便于脚本解析）：
 *   method=cfr result=ok ms=12 bytes=73400320 errno=0
 *   method=cfr result=fail ms=3 bytes=0 errno=EOPNOTSUPP
 *
 * 退出码：
 *   0  该路径可用
 *   3  该路径不可用（errno 已打印）
 *   1  参数错误
 *
 * 注意：调用方应当给它加超时（建议 10~15 秒），并且要有「超时后不等待」
 *       的兜底 —— 真卡住时进程杀不掉。
 * ==========================================================================*/
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <linux/fs.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/sendfile.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <time.h>
#include <unistd.h>

#ifndef __NR_copy_file_range
/* x86-64 上 copy_file_range 的系统调用号；头文件过旧时兜底 */
#define __NR_copy_file_range 326
#endif

#define RW_BUF (80 * 1024) /* 与 .NET 的 CopyFile_ReadWrite 一致 */

static long long now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (long long)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static const char *errname(int e)
{
    switch (e) {
    case 0:          return "0";
    case EOPNOTSUPP: return "EOPNOTSUPP";
    case EXDEV:      return "EXDEV";
    case EINVAL:     return "EINVAL";
    case ENOSYS:     return "ENOSYS";
    case ENOTTY:     return "ENOTTY";
    case EIO:        return "EIO";
    case ENOSPC:     return "ENOSPC";
    case EPERM:      return "EPERM";
    default:         return "ERR";
    }
}

static int probe_ficlone(int in, int out, long long want, long long *total)
{
    if (want == 0) { *total = 0; return 0; }
    if (ioctl(out, FICLONE, in) == 0) { *total = want; return 0; }
    return errno ? errno : EIO;
}

static int probe_cfr(int in, int out, long long want, long long *total)
{
    long long left = want;
    *total = 0;
    while (left > 0) {
        long r = syscall(__NR_copy_file_range, in, (void *)0, out, (void *)0,
                         (size_t)left, (unsigned int)0);
        if (r > 0) { *total += r; left -= r; continue; }
        if (r == 0) return left == 0 ? 0 : EIO; /* 0 字节且未拷完 = 无法推进 */
        return errno ? errno : EIO;
    }
    return 0;
}

static int probe_sendfile(int in, int out, long long want, long long *total)
{
    long long left = want;
    *total = 0;
    while (left > 0) {
        ssize_t r = sendfile(out, in, NULL, (size_t)left);
        if (r > 0) { *total += r; left -= r; continue; }
        if (r == 0) return left == 0 ? 0 : EIO;
        return errno ? errno : EIO;
    }
    return 0;
}

static int probe_rw(int in, int out, long long want, long long *total)
{
    char *buf = (char *)malloc(RW_BUF);
    int rc = 0;
    (void)want;
    *total = 0;
    if (!buf) return ENOMEM;
    for (;;) {
        ssize_t r = read(in, buf, RW_BUF);
        if (r < 0) { rc = errno ? errno : EIO; break; }
        if (r == 0) break;
        ssize_t off = 0;
        while (off < r) {
            ssize_t w = write(out, buf + off, (size_t)(r - off));
            if (w < 0) { rc = errno ? errno : EIO; break; }
            off += w;
        }
        if (rc) break;
        *total += r;
    }
    free(buf);
    return rc;
}

int main(int argc, char **argv)
{
    const char *method, *src, *dst;
    int in, out, err = 0;
    struct stat st;
    long long t0, t1, total = 0, want;

    if (argc != 4) {
        fprintf(stderr, "usage: copy-probe <ficlone|cfr|sendfile|rw> <src> <dst>\n");
        return 1;
    }
    method = argv[1];
    src = argv[2];
    dst = argv[3];

    in = open(src, O_RDONLY);
    if (in < 0) {
        printf("method=%s result=fail ms=0 bytes=0 errno=ESRC\n", method);
        return 3;
    }
    if (fstat(in, &st) != 0 || !S_ISREG(st.st_mode)) {
        close(in);
        printf("method=%s result=fail ms=0 bytes=0 errno=ENOTREG\n", method);
        return 3;
    }
    want = (long long)st.st_size;

    /* 每次都从干净的目标文件开始，避免上一次的残留影响判断 */
    unlink(dst);
    out = open(dst, O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (out < 0) {
        close(in);
        printf("method=%s result=fail ms=0 bytes=0 errno=EDST\n", method);
        return 3;
    }

    t0 = now_ms();
    if (strcmp(method, "ficlone") == 0)       err = probe_ficlone(in, out, want, &total);
    else if (strcmp(method, "cfr") == 0)      err = probe_cfr(in, out, want, &total);
    else if (strcmp(method, "sendfile") == 0) err = probe_sendfile(in, out, want, &total);
    else if (strcmp(method, "rw") == 0)       err = probe_rw(in, out, want, &total);
    else {
        fprintf(stderr, "unknown method: %s\n", method);
        close(in);
        close(out);
        unlink(dst);
        return 1;
    }
    t1 = now_ms();

    close(in);
    close(out);

    printf("method=%s result=%s ms=%lld bytes=%lld errno=%s\n",
           method, err == 0 ? "ok" : "fail", t1 - t0, total, errname(err));
    return err == 0 ? 0 : 3;
}
