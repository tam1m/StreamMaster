﻿using Microsoft.Extensions.Logging;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using StreamMaster.SchedulesDirectAPI.Domain.Enums;
using StreamMaster.SchedulesDirectAPI.Helpers;

using StreamMasterDomain.Common;
using StreamMasterDomain.Dto;
using StreamMasterDomain.Enums;

using System.Collections.Concurrent;

namespace StreamMaster.SchedulesDirectAPI;
public partial class SchedulesDirect
{
    private static readonly SemaphoreSlim downloadSemaphore = new(MaxParallelDownloads);

    public async Task<HttpResponseMessage> GetSdImage(string uri)
    {
        return await schedulesDirectAPI.GetSdImage(uri);
    }
    public async Task<List<ProgramMetadata>?> GetArtworkAsync(string[] request)
    {
        DateTime dtStart = DateTime.Now;
        List<ProgramMetadata>? ret = await schedulesDirectAPI.GetApiResponse<List<ProgramMetadata>>(APIMethod.POST, "metadata/programs/", request);
        if (ret != null)
        {
            logger.LogDebug($"Successfully retrieved artwork info for {ret.Count}/{request.Length} programs. ({DateTime.Now - dtStart:G})");
        }
        else
        {
            logger.LogDebug($"Did not receive a response from Schedules Direct for artwork info of {request.Length} programs. ({DateTime.Now - dtStart:G})");
        }

        return ret;
    }

    //public HttpResponseMessage GetImage(string uri, DateTimeOffset ifModifiedSince)
    //{
    //    return schedulesDirectAPI.GetSdImage(uri[1..], ifModifiedSince).Result;
    //}
    public async Task<List<string>?> GetCustomLogosFromServerAsync(string server)
    {
        return await schedulesDirectAPI.GetApiResponse<List<string>>(APIMethod.GET, server);
    }


    private void DownloadImageResponses(List<string> imageQueue, ConcurrentBag<ProgramMetadata> programMetadata, int start = 0)
    {
        // reject 0 requests
        if (imageQueue.Count - start < 1)
        {
            return;
        }

        // build the array of series to request images for
        var series = new string[Math.Min(imageQueue.Count - start, MaxImgQueries)];
        for (var i = 0; i < series.Length; ++i)
        {
            series[i] = imageQueue[start + i];
        }

        // request images from Schedules Direct
        var responses = GetArtworkAsync(series).Result;
        if (responses != null)
        {
            _ = Parallel.ForEach(responses, programMetadata.Add);
        }
        else
        {
            logger.LogInformation("Did not receive a response from Schedules Direct for artwork info of {count} programs, first entry {entry}.", series.Length, series.Any() ? series[0] : "");
            var AA = 1;
        }
    }

    private void UpdateMovieIcons(List<MxfProgram> mxfPrograms)
    {
        foreach (var prog in mxfPrograms.Where(a => a.extras.ContainsKey("artwork")))
        {
            List<ProgramArtwork> artwork = prog.extras["artwork"];
            UpdateIcons(artwork.Select(a => a.Uri), prog.Title);
        }
    }


    private void UpdateIcons(List<MxfService> Services)
    {
        foreach (var service in Services.Where(a => a.extras.ContainsKey("logo")))
        {
            StationImage artwork = service.extras["logo"];
            UpdateIcon(artwork.Url, service.CallSign);
        }
    }
    private void UpdateIcons(List<MxfProgram> mxfPrograms)
    {
        foreach (var prog in mxfPrograms.Where(a => a.extras.ContainsKey("artwork")))
        {
            List<ProgramArtwork> artwork = prog.extras["artwork"];
            UpdateIcons(artwork.Select(a => a.Uri), prog.Title);
        }
    }

    private async Task<bool> DownloadStationLogos(CancellationToken cancellationToken)
    {
        Setting setting = memoryCache.GetSetting();
        if (!setting.SDSettings.SDEnabled)
        {
            return false;
        }

        if (StationLogosToDownload.Count == 0)
        {
            return false;
        }

        var semaphore = new SemaphoreSlim(MaxParallelDownloads);

        var tasks = StationLogosToDownload.Select(async serviceLogo =>
        {
            await semaphore.WaitAsync(cancellationToken);

            try
            {
                var logoPath = serviceLogo.Value[0];
                if (!File.Exists(logoPath))
                {
                    var (width, height) = await DownloadSdLogoAsync(serviceLogo.Value[1], logoPath, cancellationToken).ConfigureAwait(false);
                    if (width == 0)
                    {
                        return;
                    }
                    serviceLogo.Key.mxfGuideImage = schedulesDirectData.FindOrCreateGuideImage(logoPath);

                    //if (File.Exists(logoPath))
                    //{
                    serviceLogo.Key.extras["logo"].Height = height;
                    serviceLogo.Key.extras["logo"].Width = width;
                    _ = StationLogosToDownload.Remove(serviceLogo);
                    //}
                }
            }
            finally
            {
                _ = semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        return true;
    }

    private async Task<(int width, int height)> DownloadSdLogoAsync(string uri, string filePath, CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient();
            var response = await httpClient.GetAsync(uri, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

                using var image = await Image.LoadAsync<Rgba32>(stream, cancellationToken).ConfigureAwait(false);
                using var cropImg = SDHelpers.CropAndResizeImage(image);
                if (cropImg == null)
                {
                    return (0, 0);
                }
                using var outputFileStream = File.Create(filePath);
                var a = image.Metadata.DecodedImageFormat;
                cropImg.Save(outputFileStream, image.Metadata.DecodedImageFormat);
                return (cropImg.Width, cropImg.Height);
            }
            else
            {
                logger.LogError($"HTTP request failed with status code: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError($"An exception occurred during DownloadSDLogoAsync(). Message:{FileUtil.ReportExceptionMessages(ex)}");
        }
        return (0, 0);
    }

    private void UpdateSeasonIcons(List<MxfSeason> mxfSeasons)
    {
        foreach (var prog in mxfSeasons.Where(a => a.extras.ContainsKey("artwork")))
        {
            List<ProgramArtwork> artwork = prog.extras["artwork"];
            UpdateIcons(artwork.Select(a => a.Uri), prog.Title);
        }
    }
    private void UpdateIcons(List<MxfSeriesInfo> mxfSeriesInfos)
    {
        foreach (var prog in mxfSeriesInfos.Where(a => a.extras.ContainsKey("artwork")))
        {
            List<ProgramArtwork> artwork = prog.extras["artwork"];
            UpdateIcons(artwork.Select(a => a.Uri), prog.Title);
        }
    }
    private void UpdateIcon(string artworkUri, string title)
    {
        if (string.IsNullOrEmpty(artworkUri))
        {
            return;
        }

        List<IconFileDto> icons = memoryCache.Icons();

        if (icons.Any(a => a.SMFileType == SMFileTypes.SDImage && a.Source == artworkUri))
        {
            return;
        }

        icons.Add(new IconFileDto { Id = icons.Count, Source = artworkUri, SMFileType = SMFileTypes.SDImage, Name = title });

        memoryCache.SetIcons(icons);
    }

    private void UpdateIcons(IEnumerable<string> artworkUris, string title)
    {
        if (!artworkUris.Any())
        {
            return;
        }

        List<IconFileDto> icons = memoryCache.Icons();

        foreach (var artworkUri in artworkUris)
        {
            UpdateIcon(artworkUri, title);

        }
        memoryCache.SetIcons(icons);
    }

    //private async Task<bool> DownloadSdLogo2(string uri, string filePath, CancellationToken cancellationToken)
    //{

    //    filePath = FileUtil.CleanUpFileName(filePath);
    //    try
    //    {
    //        if (!await EnsureToken(cancellationToken).ConfigureAwait(false))
    //        {
    //            return false;
    //        }

    //        Setting setting = await settingsService.GetSettingsAsync(cancellationToken);

    //        using HttpResponseMessage response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);

    //        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
    //        {
    //            return false;
    //        }

    //        if (response.IsSuccessStatusCode)
    //        {
    //            Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    //            if (stream != null)
    //            {
    //                //var cropImg = await CropAndResizeImageAsync(stream);

    //                //// Crop and save image
    //                //using (var outputStream = File.Create(filePath))
    //                //{
    //                //    cropImg.Save(outputStream, new JpegEncoder());
    //                //}


    //                using var outputStream = File.Create(filePath);
    //                await stream.CopyToAsync(outputStream, cancellationToken);
    //                stream.Close();
    //                stream.Dispose();
    //                logger.LogDebug($"Downloaded image from {uri} to {filePath}: {response.StatusCode}");
    //                return true;
    //            }
    //        }
    //        else
    //        {
    //            logger.LogError($"Failed to download image from {uri} to {filePath}: {response.StatusCode}");
    //        }

    //    }
    //    catch (Exception ex)
    //    {
    //        logger.LogError(ex, "Failed to download image from {Url} to {FileName}.", uri, filePath);
    //    }
    //    return false;
    //}
}