﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Common.IO;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.FileOrganization;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.FileOrganization;
using MediaBrowser.Model.Logging;
using System.Globalization;
using System.Threading.Tasks;

namespace MediaBrowser.Server.Implementations.FileOrganization
{
    public class EpisodeFileOrganizer
    {
        private readonly IDirectoryWatchers _directoryWatchers;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IFileOrganizationService _organizationService;
        private readonly IServerConfigurationManager _config;

        private  readonly CultureInfo _usCulture = new CultureInfo("en-US");

        public EpisodeFileOrganizer(IFileOrganizationService organizationService, IServerConfigurationManager config, IFileSystem fileSystem, ILogger logger, ILibraryManager libraryManager, IDirectoryWatchers directoryWatchers)
        {
            _organizationService = organizationService;
            _config = config;
            _fileSystem = fileSystem;
            _logger = logger;
            _libraryManager = libraryManager;
            _directoryWatchers = directoryWatchers;
        }

        public async Task<FileOrganizationResult> OrganizeEpisodeFile(string path, TvFileOrganizationOptions options, bool overwriteExisting)
        {
            _logger.Info("Sorting file {0}", path);

            var result = new FileOrganizationResult
            {
                Date = DateTime.UtcNow,
                OriginalPath = path,
                OriginalFileName = Path.GetFileName(path),
                Type = FileOrganizerType.Episode
            };

            var seriesName = TVUtils.GetSeriesNameFromEpisodeFile(path);

            if (!string.IsNullOrEmpty(seriesName))
            {
                var season = TVUtils.GetSeasonNumberFromEpisodeFile(path);

                result.ExtractedSeasonNumber = season;

                if (season.HasValue)
                {
                    // Passing in true will include a few extra regex's
                    var episode = TVUtils.GetEpisodeNumberFromFile(path, true);

                    result.ExtractedEpisodeNumber = episode;

                    if (episode.HasValue)
                    {
                        _logger.Debug("Extracted information from {0}. Series name {1}, Season {2}, Episode {3}", path, seriesName, season, episode);

                        var endingEpisodeNumber = TVUtils.GetEndingEpisodeNumberFromFile(path);

                        result.ExtractedEndingEpisodeNumber = endingEpisodeNumber;

                        OrganizeEpisode(path, seriesName, season.Value, episode.Value, endingEpisodeNumber, options, overwriteExisting, result);
                    }
                    else
                    {
                        var msg = string.Format("Unable to determine episode number from {0}", path);
                        result.Status = FileSortingStatus.Failure;
                        result.StatusMessage = msg;
                        _logger.Warn(msg);
                    }
                }
                else
                {
                    var msg = string.Format("Unable to determine season number from {0}", path);
                    result.Status = FileSortingStatus.Failure;
                    result.StatusMessage = msg;
                    _logger.Warn(msg);
                }
            }
            else
            {
                var msg = string.Format("Unable to determine series name from {0}", path);
                result.Status = FileSortingStatus.Failure;
                result.StatusMessage = msg;
                _logger.Warn(msg);
            }

            await _organizationService.SaveResult(result, CancellationToken.None).ConfigureAwait(false);

            return result;
        }

        public async Task<FileOrganizationResult> OrganizeWithCorrection(EpisodeFileOrganizationRequest request, TvFileOrganizationOptions options)
        {
            var result = _organizationService.GetResult(request.ResultId);

            var series = (Series)_libraryManager.GetItemById(new Guid(request.SeriesId));

            OrganizeEpisode(result.OriginalPath, series, request.SeasonNumber, request.EpisodeNumber, request.EndingEpisodeNumber, _config.Configuration.TvFileOrganizationOptions, true, result);

            await _organizationService.SaveResult(result, CancellationToken.None).ConfigureAwait(false);

            return result;
        }

        private void OrganizeEpisode(string sourcePath, string seriesName, int seasonNumber, int episodeNumber, int? endingEpiosdeNumber, TvFileOrganizationOptions options, bool overwriteExisting, FileOrganizationResult result)
        {
            var series = GetMatchingSeries(seriesName, result);

            if (series == null)
            {
                var msg = string.Format("Unable to find series in library matching name {0}", seriesName);
                result.Status = FileSortingStatus.Failure;
                result.StatusMessage = msg;
                _logger.Warn(msg);
                return;
            }

            OrganizeEpisode(sourcePath, series, seasonNumber, episodeNumber, endingEpiosdeNumber, options, overwriteExisting, result);
        }

        private void OrganizeEpisode(string sourcePath, Series series, int seasonNumber, int episodeNumber, int? endingEpiosdeNumber, TvFileOrganizationOptions options, bool overwriteExisting, FileOrganizationResult result)
        {
            _logger.Info("Sorting file {0} into series {1}", sourcePath, series.Path);

            // Proceed to sort the file
            var newPath = GetNewPath(sourcePath, series, seasonNumber, episodeNumber, endingEpiosdeNumber, options);

            if (string.IsNullOrEmpty(newPath))
            {
                var msg = string.Format("Unable to sort {0} because target path could not be determined.", sourcePath);
                result.Status = FileSortingStatus.Failure;
                result.StatusMessage = msg;
                _logger.Warn(msg);
                return;
            }

            _logger.Info("Sorting file {0} to new path {1}", sourcePath, newPath);
            result.TargetPath = newPath;

            var existing = GetDuplicatePaths(result.TargetPath, series, seasonNumber, episodeNumber);

            if (!overwriteExisting && existing.Count > 0)
            {
                result.Status = FileSortingStatus.SkippedExisting;
                result.StatusMessage = string.Empty;
                return;
            }

            PerformFileSorting(options, result);
        }

        private List<string> GetDuplicatePaths(string targetPath, Series series, int seasonNumber, int episodeNumber)
        {
            var list = new List<string>();

            if (File.Exists(targetPath))
            {
                list.Add(targetPath);
            }

            return list;
        }

        private void PerformFileSorting(TvFileOrganizationOptions options, FileOrganizationResult result)
        {
            _directoryWatchers.TemporarilyIgnore(result.TargetPath);

            Directory.CreateDirectory(Path.GetDirectoryName(result.TargetPath));

            var copy = File.Exists(result.TargetPath);
            
            try
            {
                if (copy)
                {
                    File.Copy(result.OriginalPath, result.TargetPath, true);
                }
                else
                {
                    File.Move(result.OriginalPath, result.TargetPath);
                }

                result.Status = FileSortingStatus.Success;
                result.StatusMessage = string.Empty;
            }
            catch (Exception ex)
            {
                var errorMsg = string.Format("Failed to move file from {0} to {1}", result.OriginalPath, result.TargetPath);

                result.Status = FileSortingStatus.Failure;
                result.StatusMessage = errorMsg;
                _logger.ErrorException(errorMsg, ex);

                return;
            }
            finally
            {
                _directoryWatchers.RemoveTempIgnore(result.TargetPath);
            }

            if (copy)
            {
                try
                {
                    File.Delete(result.OriginalPath);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error deleting {0}", ex, result.OriginalPath);
                }
            }
        }

        private Series GetMatchingSeries(string seriesName, FileOrganizationResult result)
        {
            int? yearInName;
            var nameWithoutYear = seriesName;
            NameParser.ParseName(nameWithoutYear, out nameWithoutYear, out yearInName);

            result.ExtractedName = nameWithoutYear;
            result.ExtractedYear = yearInName;

            return _libraryManager.RootFolder.RecursiveChildren
                .OfType<Series>()
                .Select(i => NameUtils.GetMatchScore(nameWithoutYear, yearInName, i))
                .Where(i => i.Item2 > 0)
                .OrderByDescending(i => i.Item2)
                .Select(i => i.Item1)
                .FirstOrDefault();
        }

        /// <summary>
        /// Gets the new path.
        /// </summary>
        /// <param name="sourcePath">The source path.</param>
        /// <param name="series">The series.</param>
        /// <param name="seasonNumber">The season number.</param>
        /// <param name="episodeNumber">The episode number.</param>
        /// <param name="endingEpisodeNumber">The ending episode number.</param>
        /// <param name="options">The options.</param>
        /// <returns>System.String.</returns>
        private string GetNewPath(string sourcePath, Series series, int seasonNumber, int episodeNumber, int? endingEpisodeNumber, TvFileOrganizationOptions options)
        {
            // If season and episode numbers match
            var currentEpisodes = series.RecursiveChildren.OfType<Episode>()
                .Where(i => i.IndexNumber.HasValue &&
                            i.IndexNumber.Value == episodeNumber &&
                            i.ParentIndexNumber.HasValue &&
                            i.ParentIndexNumber.Value == seasonNumber)
                .ToList();

            if (currentEpisodes.Count == 0)
            {
                return null;
            }

            var newPath = GetSeasonFolderPath(series, seasonNumber, options);

            var episode = currentEpisodes.First();

            var episodeFileName = GetEpisodeFileName(sourcePath, series.Name, seasonNumber, episodeNumber, endingEpisodeNumber, episode.Name, options);

            newPath = Path.Combine(newPath, episodeFileName);

            return newPath;
        }

        /// <summary>
        /// Gets the season folder path.
        /// </summary>
        /// <param name="series">The series.</param>
        /// <param name="seasonNumber">The season number.</param>
        /// <param name="options">The options.</param>
        /// <returns>System.String.</returns>
        private string GetSeasonFolderPath(Series series, int seasonNumber, TvFileOrganizationOptions options)
        {
            // If there's already a season folder, use that
            var season = series
                .RecursiveChildren
                .OfType<Season>()
                .FirstOrDefault(i => i.LocationType == LocationType.FileSystem && i.IndexNumber.HasValue && i.IndexNumber.Value == seasonNumber);

            if (season != null)
            {
                return season.Path;
            }

            var path = series.Path;

            if (series.ContainsEpisodesWithoutSeasonFolders)
            {
                return path;
            }

            if (seasonNumber == 0)
            {
                return Path.Combine(path, _fileSystem.GetValidFilename(options.SeasonZeroFolderName));
            }

            var seasonFolderName = options.SeasonFolderPattern
                .Replace("%s", seasonNumber.ToString(_usCulture))
                .Replace("%0s", seasonNumber.ToString("00", _usCulture))
                .Replace("%00s", seasonNumber.ToString("000", _usCulture));

            return Path.Combine(path, _fileSystem.GetValidFilename(seasonFolderName));
        }

        private string GetEpisodeFileName(string sourcePath, string seriesName, int seasonNumber, int episodeNumber, int? endingEpisodeNumber, string episodeTitle, TvFileOrganizationOptions options)
        {
            seriesName = _fileSystem.GetValidFilename(seriesName);
            episodeTitle = _fileSystem.GetValidFilename(episodeTitle);

            var sourceExtension = (Path.GetExtension(sourcePath) ?? string.Empty).TrimStart('.');

            var pattern = endingEpisodeNumber.HasValue ? options.MultiEpisodeNamePattern : options.EpisodeNamePattern;

            var result = pattern.Replace("%sn", seriesName)
                .Replace("%s.n", seriesName.Replace(" ", "."))
                .Replace("%s_n", seriesName.Replace(" ", "_"))
                .Replace("%s", seasonNumber.ToString(_usCulture))
                .Replace("%0s", seasonNumber.ToString("00", _usCulture))
                .Replace("%00s", seasonNumber.ToString("000", _usCulture))
                .Replace("%ext", sourceExtension)
                .Replace("%en", episodeTitle)
                .Replace("%e.n", episodeTitle.Replace(" ", "."))
                .Replace("%e_n", episodeTitle.Replace(" ", "_"));

            if (endingEpisodeNumber.HasValue)
            {
                result = result.Replace("%ed", endingEpisodeNumber.Value.ToString(_usCulture))
                .Replace("%0ed", endingEpisodeNumber.Value.ToString("00", _usCulture))
                .Replace("%00ed", endingEpisodeNumber.Value.ToString("000", _usCulture));
            }

            return result.Replace("%e", episodeNumber.ToString(_usCulture))
                .Replace("%0e", episodeNumber.ToString("00", _usCulture))
                .Replace("%00e", episodeNumber.ToString("000", _usCulture));
        }
    }
}