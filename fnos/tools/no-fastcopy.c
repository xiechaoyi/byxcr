/* ============================================================================
 * no-fastcopy —— 让 .NET / MSBuild 绕过内核级的「加速拷贝」路径
 *
 * 背景：
 *   在 overlayfs（容器）、网络盘等文件系统上，内核提供的加速拷贝存在缺陷，
 *   可能让调用进程死在内核里，并触发
 *     watchdog: BUG: soft lockup - CPU#1 stuck for 52s! [Parallel Copy T:...]
 *   由于 .NET 的 SystemNative_CopyFile（pal_io.c）没有任何环境变量或
 *   AppContext 开关可以关掉这些路径，只能用 LD_PRELOAD 在 libc 层拦下来。
 *
 * 拦截之后 .NET 的行为（与 pal_io.c 的回退逻辑严格对应）：
 *
 *   * copy_file_range 返回 -1  → 源码里 `if (sent <= 0) { trySendFile = false; break; }`
 *                                即放弃 sendfile，直接落到 read/write。
 *                                因此这里用 EOPNOTSUPP，而不是 ENOSYS ——
 *                                用 ENOSYS 会让 SupportsCopyFileRange() 判定「不支持」，
 *                                于是转而尝试 sendfile，等于绕回另一条坏路径。
 *   * sendfile 返回 -1         → 源码里只有 errno 为 EINVAL / ENOSYS 才会回退到
 *                                read/write，其它 errno 会直接让拷贝**失败**。
 *                                因此这里必须用 EINVAL。
 *   * ioctl(FICLONE) 返回 -1   → .NET 只看返回值，失败即继续下一条路径。
 *
 * 只拦截「普通文件 → 普通文件」这一种组合；socket、管道等一律原样放行，
 * 避免影响运行时自身的其它行为。
 *
 * 用法：
 *   cc -shared -fPIC -O2 -o no-fastcopy.so no-fastcopy.c
 *   LD_PRELOAD=/path/no-fastcopy.so dotnet publish ...
 *
 * 可选环境变量（不设置 = 全部拦截）：
 *   BYXCR_NOFASTCOPY=none                        什么也不拦
 *   BYXCR_NOFASTCOPY=all                         全部拦截
 *   BYXCR_NOFASTCOPY=cfr,sendfile                只拦列出的方法
 *                                                （可选：ficlone / cfr / sendfile）
 * ==========================================================================*/
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <linux/fs.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/sendfile.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>

/* 环境变量里是否点名了某个方法；未设置时按 dflt 处理 */
static int shielded(const char *key, int dflt)
{
    const char *v = getenv("BYXCR_NOFASTCOPY");
    const char *p;
    size_t n;

    if (v == NULL || *v == '\0') return dflt;
    if (strcmp(v, "all") == 0) return 1;
    if (strcmp(v, "none") == 0) return 0;

    n = strlen(key);
    for (p = v; (p = strstr(p, key)) != NULL; p += n) {
        int before_ok = (p == v) || (p[-1] == ',') || (p[-1] == ' ');
        int after_ok = (p[n] == ',') || (p[n] == ' ') || (p[n] == '\0');
        if (before_ok && after_ok) return 1;
    }
    return 0;
}

static int is_regular(int fd)
{
    struct stat st;
    return fstat(fd, &st) == 0 && S_ISREG(st.st_mode);
}

ssize_t copy_file_range(int fd_in, off_t *off_in, int fd_out, off_t *off_out,
                        size_t len, unsigned int flags)
{
    static ssize_t (*real)(int, off_t *, int, off_t *, size_t, unsigned int);
    if (is_regular(fd_in) && is_regular(fd_out) && shielded("cfr", 1)) {
        errno = EOPNOTSUPP; /* 让 .NET 直接落到 read/write，且不去试 sendfile */
        return -1;
    }
    if (real == NULL)
        real = (ssize_t (*)(int, off_t *, int, off_t *, size_t, unsigned int))
            dlsym(RTLD_NEXT, "copy_file_range");
    if (real == NULL) { errno = ENOSYS; return -1; }
    return real(fd_in, off_in, fd_out, off_out, len, flags);
}

ssize_t sendfile(int out_fd, int in_fd, off_t *offset, size_t count)
{
    static ssize_t (*real)(int, int, off_t *, size_t);
    if (is_regular(out_fd) && is_regular(in_fd) && shielded("sendfile", 1)) {
        errno = EINVAL; /* .NET 只在 EINVAL/ENOSYS 时才回退，别的 errno 会让拷贝失败 */
        return -1;
    }
    if (real == NULL)
        real = (ssize_t (*)(int, int, off_t *, size_t))dlsym(RTLD_NEXT, "sendfile");
    if (real == NULL) { errno = ENOSYS; return -1; }
    return real(out_fd, in_fd, offset, count);
}

int ioctl(int fd, unsigned long request, ...)
{
    static int (*real)(int, unsigned long, ...);
    va_list ap;
    void *arg;

    va_start(ap, request);
    arg = va_arg(ap, void *);
    va_end(ap);

    /* 只精确拦 FICLONE（.NET 拷贝流程里的第一步），其余 ioctl 全部原样放行 */
    if (request == FICLONE && shielded("ficlone", 1) &&
        is_regular(fd) && arg != NULL && is_regular((int)(intptr_t)arg)) {
        errno = EOPNOTSUPP;
        return -1;
    }

    if (real == NULL)
        real = (int (*)(int, unsigned long, ...))dlsym(RTLD_NEXT, "ioctl");
    if (real == NULL) { errno = ENOSYS; return -1; }
    return real(fd, request, arg);
}
