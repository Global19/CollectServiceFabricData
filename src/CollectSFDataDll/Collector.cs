﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace CollectSFData
{
    using CollectSFData.Azure;
    using CollectSFData.Common;
    using CollectSFData.DataFile;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    public class Collector : Constants
    {
        private string[] _args;
        private bool _checkedVersion;
        private bool _initialized;
        private int _noProgressCounter = 0;
        private Timer _noProgressTimer;
        private ParallelOptions _parallelConfig;
        private Tuple<int, int, int, int, int, int, int> _progressTuple = new Tuple<int, int, int, int, int, int, int>(0, 0, 0, 0, 0, 0, 0);
        private CustomTaskManager _taskManager = new CustomTaskManager(true);

        private ConfigurationOptions Config => Instance.Config;

        public Instance Instance { get; } = Instance.Singleton();

        public Collector(string[] args, bool isConsole = false)
        {
            _args = args;
            Log.IsConsole = isConsole;
            //Initialize();
        }

        public int Collect()
        {
            return Collect(new List<string>());
        }

        public int Collect(List<string> uris = null)
        {
            try
            {
                if (!Initialize() || !InitializeKusto() || !InitializeLogAnalytics())
                {
                    return 1;
                }

                if (Config.SasEndpointInfo.IsPopulated())
                {
                    DownloadAzureData(uris);
                }
                else if (Config.IsCacheLocationPreConfigured())
                {
                    UploadCacheData(uris);
                }

                CustomTaskManager.WaitAll();
                FinalizeKusto();

                if (Config.DeleteCache && Config.IsCacheLocationPreConfigured() && Directory.Exists(Config.CacheLocation))
                {
                    Log.Info($"Deleting outputlocation: {Config.CacheLocation}");

                    try
                    {
                        Directory.Delete($"{Config.CacheLocation}", true);
                    }
                    catch (Exception ex)
                    {
                        Log.Exception($"{ex}");
                    }
                }

                Config.DisplayStatus();
                Config.SaveConfigFile();
                Instance.TotalErrors += Log.LogErrors;

                LogSummary();
                return Instance.TotalErrors;
            }
            catch (Exception ex)
            {
                Log.Exception($"{ex}");
                return 1;
            }
            finally
            {
                Log.Reset();
                CustomTaskManager.Reset();
                _noProgressTimer?.Dispose();
            }
        }

        public string DetermineClusterId()
        {
            string clusterId = string.Empty;

            if (!string.IsNullOrEmpty(Config.SasEndpointInfo.AbsolutePath))
            {
                //fabriclogs-e2fd6f05-921f-4e81-92d5-f70a648be762
                string pattern = ".+-([a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12})";

                if (Regex.IsMatch(Config.SasEndpointInfo.AbsolutePath, pattern))
                {
                    clusterId = Regex.Match(Config.SasEndpointInfo.AbsolutePath, pattern).Groups[1].Value;
                }
            }

            if (string.IsNullOrEmpty(clusterId))
            {
                TableManager tableMgr = new TableManager();

                if (tableMgr.Connect())
                {
                    clusterId = tableMgr.QueryTablesForClusterId();
                }
            }

            if (!string.IsNullOrEmpty(clusterId))
            {
                Log.Info($"cluster id:{clusterId}");
            }
            else
            {
                Log.Warning("unable to determine cluster id");
            }

            return clusterId;
        }

        public bool Initialize()
        {
            _noProgressTimer = new Timer(NoProgressCallback, null, 0, 60 * 1000);
            Log.Open();
            CustomTaskManager.Resume();

            if (_initialized)
            {
                _taskManager?.Wait();
                _taskManager = new CustomTaskManager();
                Instance.Initialize();
            }
            else
            {
                if (!Config.PopulateConfig(_args))
                {
                    Config.SaveConfigFile();
                    return false;
                }

                _initialized = true;

                Log.Info($"version: {Version}");
                _parallelConfig = new ParallelOptions { MaxDegreeOfParallelism = Config.Threads };
                ServicePointManager.DefaultConnectionLimit = Config.Threads * MaxThreadMultiplier;
                ServicePointManager.Expect100Continue = true;
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;

                ThreadPool.SetMinThreads(Config.Threads * MinThreadMultiplier, Config.Threads * MinThreadMultiplier);
                ThreadPool.SetMaxThreads(Config.Threads * MaxThreadMultiplier, Config.Threads * MaxThreadMultiplier);

            }

            return true;
        }

        private void DownloadAzureData(List<string> uris = null)
        {
            string containerPrefix = null;
            string tablePrefix = null;
            string clusterId = DetermineClusterId();

            if (!Config.FileType.Equals(FileTypesEnum.any) && !Config.FileType.Equals(FileTypesEnum.table))
            {
                containerPrefix = FileTypes.MapFileTypeUriPrefix(Config.FileType);

                if (!string.IsNullOrEmpty(clusterId))
                {
                    // 's-' in prefix may not always be correct
                    containerPrefix += "s-" + clusterId;
                }

                tablePrefix = containerPrefix + clusterId?.Replace("-", "");
            }

            if (Config.FileType == FileTypesEnum.table)
            {
                TableManager tableMgr = new TableManager()
                {
                    IngestCallback = (exportedFile) => { QueueForIngest(exportedFile); }
                };

                if (tableMgr.Connect())
                {
                    tableMgr.DownloadTables(tablePrefix);
                }
            }
            else
            {
                BlobManager blobMgr = new BlobManager()
                {
                    IngestCallback = (sourceFileUri) => { QueueForIngest(sourceFileUri); },
                    ReturnSourceFileLink = (Config.IsKustoConfigured() & Config.KustoUseBlobAsSource) | Config.FileType == FileTypesEnum.exception
                };

                if (blobMgr.Connect())
                {
                    if (uris?.Count > 0)
                    {
                        blobMgr.DownloadFiles(uris);
                    }
                    else
                    {
                        blobMgr.DownloadContainers(containerPrefix);
                    }
                }
            }
        }

        private void FinalizeKusto()
        {
            if (Config.IsKustoConfigured() && !Instance.Kusto.Complete())
            {
                Log.Warning($"there may have been errors during kusto import. {Config.CacheLocation} has *not* been deleted.");
            }
            else if (Config.IsKustoConfigured())
            {
                Log.Last($"{DataExplorer}/clusters/{Instance.Kusto.Endpoint.ClusterName}/databases/{Instance.Kusto.Endpoint.DatabaseName}", ConsoleColor.Cyan);
            }
        }

        private bool InitializeKusto()
        {
            if (Config.IsKustoConfigured() | Config.IsKustoPurgeRequested())
            {
                return Instance.Kusto.Connect();
            }

            return true;
        }

        private bool InitializeLogAnalytics()
        {
            if (Config.IsLogAnalyticsConfigured() | Config.LogAnalyticsCreate | Config.IsLogAnalyticsPurgeRequested())
            {
                return Instance.LogAnalytics.Connect();
            }

            return true;
        }

        private void LogSummary()
        {
            Log.Last($"{Instance.TotalFilesEnumerated} files enumerated.");
            Log.Last($"{Instance.TotalFilesMatched} files matched.");
            Log.Last($"{Instance.TotalFilesDownloaded} files downloaded.");
            Log.Last($"{Instance.TotalFilesSkipped} files skipped.");
            Log.Last($"{Instance.TotalFilesFormatted} files formatted.");
            Log.Last($"{Instance.TotalErrors} errors.");
            Log.Last($"{Instance.TotalRecords} records.");

            if (Instance.TotalFilesEnumerated > 0)
            {
                if (Config.FileType != FileTypesEnum.table)
                {
                    DateTime discoveredMinDateTime = new DateTime(DiscoveredMinDateTicks);
                    DateTime discoveredMaxDateTime = new DateTime(DiscoveredMaxDateTicks);

                    Log.Last($"discovered time range: {discoveredMinDateTime.ToString("o")} - {discoveredMaxDateTime.ToString("o")}", ConsoleColor.Green);

                    if (discoveredMinDateTime.Ticks > Config.EndTimeUtc.Ticks | discoveredMaxDateTime.Ticks < Config.StartTimeUtc.Ticks)
                    {
                        Log.Last($"error: configured time range not within discovered time range. configured time range: {Config.StartTimeUtc} - {Config.EndTimeUtc}", ConsoleColor.Red);
                    }
                }

                if (Instance.TotalFilesMatched + Instance.TotalRecords == 0
                    && (!string.IsNullOrEmpty(Config.UriFilter) | !string.IsNullOrEmpty(Config.ContainerFilter) | !string.IsNullOrEmpty(Config.NodeFilter)))
                {
                    Log.Last("0 records found and filters are configured. verify filters and / or try time range are correct.", ConsoleColor.Yellow);
                }
                else if (Instance.TotalFilesMatched + Instance.TotalRecords == 0)
                {
                    Log.Last("0 records found. verify time range is correct.", ConsoleColor.Yellow);
                }
            }
            else
            {
                Log.Last("0 files enumerated.", ConsoleColor.Red);
            }

            // do random (10%) version check
            if (Log.IsConsole && !_checkedVersion && new Random().Next(1, 11) == 10)
            {
                _checkedVersion = true;
                Config.CheckReleaseVersion();
            }

            Log.Last($"total execution time in minutes: { (DateTime.Now - Instance.StartTime).TotalMinutes.ToString("F2") }");
        }

        private void NoProgressCallback(object state)
        {
            Log.Highlight($"checking progress {_noProgressCounter} of {Config.NoProgressTimeoutMin}.");

            if (Config.NoProgressTimeoutMin < 1)
            {
                _noProgressTimer.Dispose();
            }

            Tuple<int, int, int, int, int, int, int> tuple = new Tuple<int, int, int, int, int, int, int>(
                Instance.TotalErrors,
                Instance.TotalFilesDownloaded,
                Instance.TotalFilesEnumerated,
                Instance.TotalFilesFormatted,
                Instance.TotalFilesMatched,
                Instance.TotalFilesSkipped,
                Instance.TotalRecords);

            if (tuple.Equals(_progressTuple))
            {
                if (_noProgressCounter >= Config.NoProgressTimeoutMin)
                {
                    if (Config.IsKustoConfigured())
                    {
                        Log.Warning($"kusto ingesting:", Instance.Kusto.IngestFileObjectsPending);
                        Log.Warning($"kusto failed:", Instance.Kusto.IngestFileObjectsFailed);
                    }

                    LogSummary();

                    string message = $"no progress timeout reached {Config.NoProgressTimeoutMin}. exiting application.";
                    Log.Error(message);
                    Log.Reset();
                    throw new TimeoutException(message);
                }

                ++_noProgressCounter;
            }
            else
            {
                _noProgressCounter = 0;
                _progressTuple = tuple;
            }
        }

        private void QueueForIngest(FileObject fileObject)
        {
            Log.Debug("enter");

            if (Config.IsKustoConfigured() | Config.IsLogAnalyticsConfigured())
            {
                if (Config.IsKustoConfigured())
                {
                    _taskManager.QueueTaskAction(() => Instance.Kusto.AddFile(fileObject));
                }

                if (Config.IsLogAnalyticsConfigured())
                {
                    _taskManager.QueueTaskAction(() => Instance.LogAnalytics.AddFile(fileObject));
                }
            }
            else
            {
                _taskManager.QueueTaskAction(() => Instance.FileMgr.ProcessFile(fileObject));
            }
        }

        private void UploadCacheData(List<string> uris)
        {
            Log.Info("enter");
            List<string> files = new List<string>();

            if (uris.Count > 0)
            {
                foreach(string file in uris)
                {
                    if(File.Exists(file))
                    {
                        Log.Info($"adding file to list: {file}");
                        files.Add(file);
                    }
                    else
                    {
                        Log.Warning($"file does not exist: {file}");
                    }
                }
            }
            else
            {
                switch (Config.FileType)
                {
                    case FileTypesEnum.counter:
                        files = Directory.GetFiles(Config.CacheLocation, $"*{PerfCtrExtension}", SearchOption.AllDirectories).ToList();

                        if (files.Count < 1)
                        {
                            files = Directory.GetFiles(Config.CacheLocation, $"*{PerfCsvExtension}", SearchOption.AllDirectories).ToList();
                        }

                        break;

                    case FileTypesEnum.setup:
                        files = Directory.GetFiles(Config.CacheLocation, $"*{SetupExtension}", SearchOption.AllDirectories).ToList();

                        break;

                    case FileTypesEnum.table:
                        files = Directory.GetFiles(Config.CacheLocation, $"*{TableExtension}", SearchOption.AllDirectories).ToList();

                        break;

                    case FileTypesEnum.trace:
                        files = Directory.GetFiles(Config.CacheLocation, $"*{TraceFileExtension}{ZipExtension}", SearchOption.AllDirectories).ToList();

                        if (files.Count < 1)
                        {
                            files = Directory.GetFiles(Config.CacheLocation, $"*{TraceFileExtension}", SearchOption.AllDirectories).ToList();
                        }

                        break;

                    default:
                        Log.Warning($"invalid filetype for cache upload. returning {Config.FileType}");
                        return;
                }
            }

            if (files.Count < 1)
            {
                Log.Error($"configuration set to upload cache files from 'cachelocation' {Config.CacheLocation} but no files found");
            }

            foreach (string file in files)
            {
                FileObject fileObject = new FileObject(file, Config.CacheLocation);
                Log.Info($"adding file: {fileObject.FileUri}", ConsoleColor.Green);

                if (!Config.List)
                {
                    QueueForIngest(fileObject);
                }
            }
        }
    }
}