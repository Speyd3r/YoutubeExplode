using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Utils.Extensions;

namespace YoutubeExplode.Videos.ClosedCaptions;

/// <summary>
/// Operations related to closed captions of YouTube videos.
/// </summary>
public class ClosedCaptionClient
{
    private readonly ClosedCaptionController _controller;

    /// <summary>
    /// Initializes an instance of <see cref="ClosedCaptionClient" />.
    /// </summary>
    public ClosedCaptionClient(HttpClient http) => _controller = new ClosedCaptionController(http);

    private async IAsyncEnumerable<ClosedCaptionTrackInfo> GetClosedCaptionTrackInfosAsync(
        VideoId videoId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Use the TVHTML5 client instead of ANDROID_TESTSUITE because the latter doesn't provide closed captions
        var playerResponse = await _controller.GetPlayerResponseAsync(videoId, null, cancellationToken);

        foreach (var trackData in playerResponse.ClosedCaptionTracks)
        {
            var url =
                trackData.Url ??
                throw new YoutubeExplodeException("Could not extract track URL.");

            var languageCode =
                trackData.LanguageCode ??
                throw new YoutubeExplodeException("Could not extract track language code.");

            var languageName =
                trackData.LanguageName ??
                throw new YoutubeExplodeException("Could not extract track language name.");

            yield return new ClosedCaptionTrackInfo(
                url,
                new Language(languageCode, languageName),
                trackData.IsAutoGenerated
            );
        }
    }

    /// <summary>
    /// Gets the manifest that lists available closed caption tracks for the specified video.
    /// </summary>
    public async ValueTask<ClosedCaptionManifest> GetManifestAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default) =>
        new(await GetClosedCaptionTrackInfosAsync(videoId, cancellationToken));

    private async IAsyncEnumerable<ClosedCaption> GetClosedCaptionsAsync(
        ClosedCaptionTrackInfo trackInfo,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await _controller.GetClosedCaptionTrackResponseAsync(trackInfo.Url, cancellationToken);

        foreach (var captionData in response.Captions)
        {
            // Captions may have no text, but we should still include them to stay consistent
            // with YouTube player behavior where captions are still displayed even if they're empty.
            // https://github.com/Tyrrrz/YoutubeExplode/issues/671
            var text = captionData.Text ?? "";

            // Auto-generated captions may be missing offset or duration.
            // https://github.com/Tyrrrz/YoutubeExplode/discussions/619
            if (captionData.Offset is not { } offset ||
                captionData.Duration is not { } duration)
            {
                continue;
            }

            var parts = captionData.Parts.Select(p =>
            {
                // Caption parts may have no text, but we should still include them to stay consistent
                // with YouTube player behavior where captions are still displayed even if they're empty.
                // https://github.com/Tyrrrz/YoutubeExplode/issues/671
                var partText = p.Text ?? "";

                var partOffset =
                    p.Offset ??
                    throw new YoutubeExplodeException("Could not extract caption part offset.");

                return new ClosedCaptionPart(partText, partOffset);
            }).ToArray();

            yield return new ClosedCaption(
                text,
                offset,
                duration,
                parts
            );
        }
    }

    /// <summary>
    /// Gets the closed caption track identified by the specified metadata.
    /// </summary>
    public async ValueTask<ClosedCaptionTrack> GetAsync(
        ClosedCaptionTrackInfo trackInfo,
        CancellationToken cancellationToken = default) =>
        new(await GetClosedCaptionsAsync(trackInfo, cancellationToken));

    /// <summary>
    /// Writes the closed caption track identified by the specified metadata to the specified writer.
    /// </summary>
    /// <remarks>
    /// Closed captions are written in the SRT file format.
    /// </remarks>
    public async ValueTask WriteToAsync(
        ClosedCaptionTrackInfo trackInfo,
        TextWriter writer,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var track = await GetAsync(trackInfo, cancellationToken);

        var buffer = new StringBuilder();
        for (var i = 0; i < track.Captions.Count; i++)
        {
            var caption = track.Captions[i];
            buffer.Clear();

            cancellationToken.ThrowIfCancellationRequested();

            // Line number
            buffer.AppendLine((i + 1).ToString());

            // Time start --> time end
            buffer.Append(caption.Offset.ToString(@"hh\:mm\:ss\,fff"));
            buffer.Append(" --> ");
            buffer.Append((caption.Offset + caption.Duration).ToString(@"hh\:mm\:ss\,fff"));
            buffer.AppendLine();

            // Actual text
            buffer.AppendLine(caption.Text);

            await writer.WriteLineAsync(buffer.ToString());
            progress?.Report((i + 1.0) / track.Captions.Count);
        }
    }

    /// <summary>
    /// Downloads the closed caption track identified by the specified metadata to the specified file.
    /// </summary>
    /// <remarks>
    /// Closed captions are written in the SRT file format.
    /// </remarks>
    public async ValueTask DownloadAsync(
        ClosedCaptionTrackInfo trackInfo,
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var writer = File.CreateText(filePath);
        await WriteToAsync(trackInfo, writer, progress, cancellationToken);
    }
}