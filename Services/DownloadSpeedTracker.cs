using System.Diagnostics;
using CmlLib.Core;

namespace PMCL.Services;

/// <summary>
/// Converts the aggregate byte progress reported by CmlLib into a stable
/// download speed, tolerating parallel downloads and total-size changes.
/// </summary>
public sealed class DownloadSpeedTracker
{
    private readonly object _gate = new();
    private long _lastProgressedBytes;
    private long _lastTimestamp;
    private double _smoothedBytesPerSecond;
    private bool _hasSample;

    public DownloadSpeedSample? Update(ByteProgress progress)
    {
        if (progress.TotalBytes <= 0 || progress.ProgressedBytes < 0)
            return null;

        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (!_hasSample
                || progress.ProgressedBytes < _lastProgressedBytes
                || now < _lastTimestamp)
            {
                _lastProgressedBytes = progress.ProgressedBytes;
                _lastTimestamp = now;
                _hasSample = true;
                _smoothedBytesPerSecond = 0;
                return new DownloadSpeedSample(0, progress.ProgressedBytes, progress.TotalBytes);
            }

            var elapsedSeconds = Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
            var deltaBytes = progress.ProgressedBytes - _lastProgressedBytes;
            if (elapsedSeconds < 0.05 || deltaBytes < 0)
                return new DownloadSpeedSample(
                    _smoothedBytesPerSecond,
                    progress.ProgressedBytes,
                    progress.TotalBytes);

            _lastProgressedBytes = progress.ProgressedBytes;
            _lastTimestamp = now;

            if (deltaBytes > 0)
            {
                var instantSpeed = deltaBytes / elapsedSeconds;
                _smoothedBytesPerSecond = _smoothedBytesPerSecond <= 0
                    ? instantSpeed
                    : (_smoothedBytesPerSecond * 0.65) + (instantSpeed * 0.35);
            }
            else if (_smoothedBytesPerSecond > 0)
            {
                // No bytes arrived during this interval; let the UI speed decay.
                _smoothedBytesPerSecond *= 0.7;
            }

            return new DownloadSpeedSample(
                _smoothedBytesPerSecond,
                progress.ProgressedBytes,
                progress.TotalBytes);
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            bytes = 0;

        const double kilo = 1024;
        const double mega = kilo * 1024;
        const double giga = mega * 1024;

        return bytes switch
        {
            >= (long)giga => $"{bytes / giga:0.00} GB",
            >= (long)mega => $"{bytes / mega:0.0} MB",
            >= (long)kilo => $"{bytes / kilo:0.0} KB",
            _ => $"{bytes} B"
        };
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond <= 0)
            return string.Empty;

        return FormatBytes((long)bytesPerSecond) + "/s";
    }
}

public readonly record struct DownloadSpeedSample(
    double BytesPerSecond,
    long ProgressedBytes,
    long TotalBytes);
