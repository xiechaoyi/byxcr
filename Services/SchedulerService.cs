using Byxcr.Configuration;
using Byxcr.Core;
using Byxcr.Persistence;
using Byxcr.Logging;

namespace Byxcr.Services;

/// <summary>
/// 定时调度器：周期性扫描数据库，按每个镜像各自的检查频率触发同步。
/// </summary>
public sealed class SchedulerService
{
    private readonly AppConfig _config;
    private readonly ImageStore _images;
    private readonly SyncRecordStore _records;
    private readonly ImageSyncService _sync;

    public SchedulerService(AppConfig config, ImageStore images, SyncRecordStore records, ImageSyncService sync)
    {
        _config = config;
        _images = images;
        _records = records;
        _sync = sync;
    }

    /// <summary>定时调度的并发硬上限：始终小于 5，避免任务列表很长时一次性铺开太多下载。</summary>
    public const int MaxScheduleConcurrency = 4;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var scanSeconds = Math.Max(5, _config.Sync.ScanIntervalSeconds);
        Log.Note($"调度器已启动：扫描周期 {scanSeconds}s，并发 {ScheduleConcurrency()}（上限 {MaxScheduleConcurrency}），"
                 + $"检查策略 {_config.Sync.CheckStrategy}"
                 + (_config.Sync.IgnoreArchiveCheck ? "（忽略归档检查）" : string.Empty)
                 + $"，归档目录 {_config.Storage.ImageRoot}");

        // 兜底：清理上次异常退出（崩溃 / 被杀）留下的同步租约
        try
        {
            var cleared = _images.ClearExpiredLeases(ImageSyncService.LeaseSeconds);
            if (cleared > 0) Log.Note($"已清理 {cleared} 条超时（>{ImageSyncService.LeaseSeconds}s）的同步租约");
        }
        catch (Exception ex)
        {
            Log.Debug($"清理超时同步租约失败：{ex.Message}");
        }

        if (!_config.Sync.Enabled)
        {
            Log.Warn("Sync.Enabled = false，调度器不会自动触发同步（仍可用 byxcr sync 手动同步）");
        }

        if (_config.Sync.SyncOnStartup && _config.Sync.Enabled)
        {
            Log.Note("Sync.SyncOnStartup = true，启动时立即同步一轮");
            await RunOnceAsync("startup", force: false, ignoreInterval: true, cancellationToken).ConfigureAwait(false);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_config.Sync.Enabled)
            {
                try
                {
                    await RunOnceAsync("schedule", force: false, ignoreInterval: false, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"调度轮询异常：{ex.Message}");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(scanSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Log.Note("调度器已停止");
    }

    /// <summary>执行一轮同步。ignoreInterval 为真时忽略间隔直接处理所有启用项。</summary>
    public async Task<List<SyncRecord>> RunOnceAsync(string trigger, bool force, bool ignoreInterval, CancellationToken cancellationToken)
    {
        List<ImageTask> tasks;
        if (ignoreInterval)
        {
            tasks = _images.GetAll().Where(task => task.Enabled).ToList();
        }
        else
        {
            tasks = _images.GetDue(DateTime.UtcNow);
        }

        if (tasks.Count == 0)
        {
            Log.Debug("没有到期的镜像任务");
            return [];
        }

        Log.Note($"本轮待同步 {tasks.Count} 个镜像：{string.Join(", ", tasks.Select(t => t.Image))}");

        var concurrency = ScheduleConcurrency();
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);
        var results = new SyncRecord[tasks.Count];

        var jobs = tasks.Select(async (task, index) =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[index] = await _sync.SyncAsync(task, trigger, force, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 取消时直接退出
            }
            catch (Exception ex)
            {
                Log.Error($"[{task.Image}] 未预期的异常：{ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(jobs).ConfigureAwait(false);

        var success = results.Count(r => r is { Status: "success" });
        var skipped = results.Count(r => r is { Status: "skipped" });
        var failed = results.Count(r => r is { Status: "failed" });
        Log.Note($"本轮结束：成功 {success}，跳过 {skipped}，失败 {failed}");

        // 顺带清理过旧的记录，避免数据库无限增长
        try
        {
            var pruned = _records.Prune(200);
            if (pruned > 0) Log.Debug($"已清理 {pruned} 条历史同步记录");
        }
        catch (Exception ex)
        {
            Log.Debug($"清理历史记录失败：{ex.Message}");
        }

        return results.Where(r => r is not null).ToList();
    }

    /// <summary>定时调度实际使用的并发数：取配置值并夹到 1..<see cref="MaxScheduleConcurrency"/>。</summary>
    private int ScheduleConcurrency() => Math.Clamp(_config.Sync.ScheduleConcurrency, 1, MaxScheduleConcurrency);
}
