﻿using Microsoft.Extensions.Logging;

using StreamMasterApplication.Common.Interfaces;
using StreamMasterApplication.Common.Models;

using StreamMasterDomain.Common;
using StreamMasterDomain.Dto;
using StreamMasterDomain.Enums;

using System.Collections.Concurrent;

namespace StreamMasterInfrastructure.MiddleWare;

public class StreamManager : IStreamManager
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, IStreamInformation> _streamInformations;

    public StreamManager(ILogger logger)
    {
        _logger = logger;
        _streamInformations = new ConcurrentDictionary<string, IStreamInformation>();
    }

    private Setting setting => FileUtil.GetSetting();

    public void Dispose()
    {
        _streamInformations.Clear();
    }

    public async Task<IStreamInformation?> GetOrCreateBuffer(ChildVideoStreamDto childVideoStreamDto)
    {
        var streamUrl = childVideoStreamDto.User_Url;
        if (_streamInformations.TryGetValue(streamUrl, out var _streamInformation))
        {
            _logger.LogInformation("Reusing buffer for stream: {StreamUrl}", setting.CleanURLs ? "url removed" : streamUrl);
            return _streamInformation;
        }

        var allStreamsCount = GetStreamsCountForM3UFile(childVideoStreamDto.M3UFileId);
        if (allStreamsCount > childVideoStreamDto.MaxStreams)
        {
            _logger.LogInformation("Max stream count {MaxStreams} reached for stream: {StreamUrl}", childVideoStreamDto.MaxStreams, setting.CleanURLs ? "url removed" : streamUrl);
            return null;
        }

        _logger.LogInformation("Creating and starting buffer for stream: {StreamUrl}", setting.CleanURLs ? "url removed" : streamUrl);

        ICircularRingBuffer buffer = new CircularRingBuffer(childVideoStreamDto);
        CancellationTokenSource cancellationTokenSource = new();

        (Stream? stream, int processId, ProxyStreamError? error) = await GetProxy(streamUrl, cancellationTokenSource.Token);

        if (stream == null || error != null)
        {
            _logger.LogError("Error GetOrCreateBuffer for {StreamUrl}", setting.CleanURLs ? "url removed" : streamUrl);
            return null;
        }

        Task streamingTask = StartVideoStreaming(stream, streamUrl, buffer, cancellationTokenSource);

        StreamInformation streamStreamInfo = new(streamUrl, buffer, streamingTask, childVideoStreamDto.M3UFileId, childVideoStreamDto.MaxStreams, processId, cancellationTokenSource);

        _streamInformations.TryAdd(streamStreamInfo.StreamUrl, streamStreamInfo);

        _logger.LogInformation("Buffer created and streaming started for: {StreamUrl}", setting.CleanURLs ? "url removed" : streamUrl);

        return streamStreamInfo;
    }

    public SingleStreamStatisticsResult GetSingleStreamStatisticsResult(string streamUrl)
    {
        if (_streamInformations.TryGetValue(streamUrl, out var _streamInformation))
        {
            return _streamInformation.RingBuffer.GetSingleStreamStatisticsResult();
        }
        return new SingleStreamStatisticsResult();
    }

    public ICollection<IStreamInformation> GetStreamInformations()
    {
        return _streamInformations.Values;
    }

    public IStreamInformation? Stop(string streamUrl)
    {
        if (_streamInformations.TryRemove(streamUrl, out var _streamInformation))
        {
            _streamInformation.Stop();
            return _streamInformation;
        }

        return null;
    }

    private async Task DelayWithCancellation(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(milliseconds, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("Task was cancelled");
            throw;
        }
    }

    private async Task<(Stream? stream, int processId, ProxyStreamError? error)> GetProxy(string streamUrl, CancellationToken cancellationToken)
    {
        Stream? stream;
        ProxyStreamError? error;
        int processId;

        if (setting.StreamingProxyType == StreamingProxyTypes.FFMpeg)
        {
            (stream, processId, error) = await StreamingProxies.GetFFMpegStream(streamUrl);
            if (processId == -1)
            {
                _logger.LogError("Error getting proxy stream for {StreamUrl}: {Error?.Message}", setting.CleanURLs ? "url removed" : streamUrl, error?.Message);
            }
        }
        else
        {
            (stream, processId, error) = await StreamingProxies.GetProxyStream(streamUrl, cancellationToken);
        }

        if (stream == null || error != null)
        {
            _logger.LogError("Error getting proxy stream for {StreamUrl}: {Error?.Message}", setting.CleanURLs ? "url removed" : streamUrl, error?.Message);
        }

        return (stream, processId, error);
    }

    private int GetStreamsCountForM3UFile(int m3uFileId)
    {
        return _streamInformations
            .Count(x => x.Value.M3UFileId == m3uFileId);
    }

    private async Task LogRetryAndDelay(int retryCount, int maxRetries, int waitTime, CancellationToken token, string streamUrl)
    {
        if (token.IsCancellationRequested)
        {
            _logger.LogInformation("Stream was cancelled for: {StreamUrl}", setting.CleanURLs ? "url removed" : streamUrl);
        }

        _logger.LogInformation("Stream received 0 bytes for stream: {StreamUrl} Retry {retryCount}/{maxRetries}",
            setting.CleanURLs ? "url removed" : streamUrl,
            retryCount,
            maxRetries);

        await DelayWithCancellation(waitTime, token);
    }

    private async Task StartVideoStreaming(Stream stream, string streamUrl, ICircularRingBuffer buffer, CancellationTokenSource cancellationToken)
    {
        var chunkSize = setting.RingBufferSizeMB * 1024 * 1000;

        _logger.LogInformation("Starting video read streaming, chunk size is {ChunkSize}, for stream: {StreamUrl}", chunkSize, setting.CleanURLs ? "url removed" : streamUrl);

        byte[] bufferChunk = new byte[chunkSize];

        var maxRetries = setting.MaxConnectRetry > 0 ? setting.MaxConnectRetry : 3;
        var waitTime = setting.MaxConnectRetryTimeMS > 0 ? setting.MaxConnectRetryTimeMS : 50;

        using (stream)
        {
            var retryCount = 0;
            while (!cancellationToken.IsCancellationRequested && retryCount < maxRetries)
            {
                try
                {
                    var bytesRead = await TryReadStream(bufferChunk, cancellationToken.Token, stream);
                    if (bytesRead == -1)
                    {
                        break;
                    }
                    if (bytesRead == 0)
                    {
                        retryCount++;
                        await LogRetryAndDelay(retryCount, maxRetries, waitTime, cancellationToken.Token, streamUrl);
                    }
                    else
                    {
                        buffer.WriteChunk(bufferChunk, bytesRead);
                        retryCount = 0;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stream error for: {StreamUrl}", setting.CleanURLs ? "url removed" : streamUrl);
                    break;
                }
            }
        }

        _logger.LogInformation("Stream stopped for: {StreamUrl}", setting.CleanURLs ? "url removed" : streamUrl);
        if (!cancellationToken.IsCancellationRequested)
            cancellationToken.Cancel();
    }

    private async Task<int> TryReadStream(byte[] bufferChunk, CancellationToken token, Stream stream)
    {
        try
        {
            if (!stream.CanRead || token.IsCancellationRequested)
            {
                _logger.LogWarning("Stream is not readable or cancelled");
                return -1;
            }

            return await stream.ReadAsync(bufferChunk);
        }
        catch (TaskCanceledException)
        {
            return -1;
        }
        catch (Exception)
        {
            throw;
        }
    }
}