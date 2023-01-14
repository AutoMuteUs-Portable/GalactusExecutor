﻿using System.Diagnostics;
using System.IO.Compression;
using System.Management;
using System.Reactive.Subjects;
using AutoMuteUsPortable.PocketBaseClient;
using AutoMuteUsPortable.Shared.Controller.Executor;
using AutoMuteUsPortable.Shared.Entity.ExecutorConfigurationBaseNS;
using AutoMuteUsPortable.Shared.Entity.ExecutorConfigurationNS;
using AutoMuteUsPortable.Shared.Entity.ProgressInfo;
using AutoMuteUsPortable.Shared.Utility;
using AutoMuteUsPortable.Shared.Utility.Dotnet.ZipFileProgressExtensionsNS;
using FluentValidation;

namespace AutoMuteUsPortable.Executor;

public class ExecutorController : ExecutorControllerBase
{
    public new static Dictionary<string, Parameter> InstallParameters = new();
    public new static Dictionary<string, Parameter> UpdateParameters = new();

    private readonly ExecutorConfiguration _executorConfiguration;
    private readonly PocketBaseClientApplication _pocketBaseClientApplication = new();
    private Process? _process;

    public ExecutorController(object executorConfiguration) : base(executorConfiguration)
    {
        #region Check variables

        var binaryDirectory = Utils.PropertyByName<string>(executorConfiguration, "binaryDirectory");
        if (binaryDirectory == null)
            throw new InvalidDataException("binaryDirectory cannot be null");

        var binaryVersion = Utils.PropertyByName<string>(executorConfiguration, "binaryVersion");
        if (binaryVersion == null)
            throw new InvalidDataException("binaryVersion cannot be null");

        var version = Utils.PropertyByName<string>(executorConfiguration, "version");
        if (version == null) throw new InvalidDataException("version cannot be null");

        ExecutorType? type = Utils.PropertyByName<ExecutorType>(executorConfiguration, "type");
        if (type == null) throw new InvalidDataException("type cannot be null");

        var environmentVariables =
            Utils.PropertyByName<Dictionary<string, string>>(executorConfiguration, "environmentVariables");
        if (environmentVariables == null) throw new InvalidDataException("environmentVariables cannot be null");

        #endregion

        #region Create ExecutorConfiguration and validate

        ExecutorConfiguration tmp = new()
        {
            version = version,
            type = (ExecutorType)type,
            binaryVersion = binaryVersion,
            binaryDirectory = binaryDirectory,
            environmentVariables = environmentVariables
        };

        var validator = new ExecutorConfigurationValidator();
        validator.ValidateAndThrow(tmp);

        _executorConfiguration = tmp;

        #endregion
    }

    public ExecutorController(object computedSimpleSettings,
        object executorConfigurationBase) : base(computedSimpleSettings, executorConfigurationBase)
    {
        #region Check variables

        var binaryDirectory = Utils.PropertyByName<string>(executorConfigurationBase, "binaryDirectory");
        if (binaryDirectory == null)
            throw new InvalidDataException("binaryDirectory cannot be null");

        var binaryVersion = Utils.PropertyByName<string>(executorConfigurationBase, "binaryVersion");
        if (binaryVersion == null)
            throw new InvalidDataException("binaryVersion cannot be null");

        var version = Utils.PropertyByName<string>(executorConfigurationBase, "version");
        if (version == null) throw new InvalidDataException("version cannot be null");

        ExecutorType? type = Utils.PropertyByName<ExecutorType>(executorConfigurationBase, "type");
        if (type == null) throw new InvalidDataException("type cannot be null");

        if (Utils.PropertyInfoByName(computedSimpleSettings, "port") == null)
            throw new InvalidDataException("port is not found in computedSimpleSettings");
        var port = Utils.PropertyByName<object>(computedSimpleSettings, "port");

        int? galactusPort = Utils.PropertyByName<int>(port!, "galactus");
        if (galactusPort == null) throw new InvalidDataException("galactusPort cannot be null");

        int? brokerPort = Utils.PropertyByName<int>(port!, "broker");
        if (brokerPort == null) throw new InvalidDataException("brokerPort cannot be null");

        int? redisPort = Utils.PropertyByName<int>(port!, "redis");
        if (redisPort == null) throw new InvalidDataException("redisPort cannot be null");


        var discordToken = Utils.PropertyByName<string>(computedSimpleSettings, "discordToken");
        if (string.IsNullOrEmpty(discordToken)) throw new InvalidDataException("discordToken cannot be null or empty");

        #endregion

        #region Create ExecutorConfiguration and validate

        ExecutorConfiguration executorConfiguration = new()
        {
            version = version,
            type = (ExecutorType)type,
            binaryVersion = binaryVersion,
            binaryDirectory = binaryDirectory,
            environmentVariables = new Dictionary<string, string>
            {
                { "DISCORD_BOT_TOKEN", discordToken },
                { "REDIS_ADDR", $"localhost:{redisPort}" },
                { "GALACTUS_PORT", galactusPort.ToString() ?? "" },
                { "BROKER_PORT", brokerPort.ToString() ?? "" },
                { "REDIS_USER", "" },
                { "REDIS_PASS", "" }
            }
        };

        var validator = new ExecutorConfigurationValidator();
        validator.ValidateAndThrow(executorConfiguration);

        _executorConfiguration = executorConfiguration;

        #endregion
    }

    public new bool IsRunning => !_process?.HasExited ?? false;

    public override async Task Run(ISubject<ProgressInfo>? progress = null)
    {
        if (IsRunning) return;

        #region Retrieve data from PocketBase

        var galactus =
            _pocketBaseClientApplication.Data.GalactusCollection.FirstOrDefault(x =>
                x.Version == _executorConfiguration.binaryVersion);
        if (galactus == null)
            throw new InvalidDataException(
                $"{_executorConfiguration.type.ToString()} {_executorConfiguration.binaryVersion} is not found in the database");
        // TODO: This doesn't work due to a bug of PocketBaseClient-csharp
        // if (galactus.CompatibleExecutors.All(x => x.Version != _executorConfiguration.version))
        //     throw new InvalidDataException(
        //         $"{_executorConfiguration.type.ToString()} {_executorConfiguration.binaryVersion} is not compatible with Executor {_executorConfiguration.version}");

        #endregion

        // TODO: Too slow. Need to be optimized or find a better way to do this
        // #region Check file integrity
        //
        // progress?.OnNext(new ProgressInfo
        // {
        //     name = $"Checking file integrity of {_executorConfiguration.type.ToString()}"
        // });
        // using (var client = new HttpClient())
        // {
        //     var hashesTxt = await client.GetStringAsync(postgresql.Hashes);
        //     var hashes = Utils.ParseHashesTxt(hashesTxt);
        //     var invalidFiles = Utils.CompareHashes(_executorConfiguration.binaryDirectory, hashes);
        //
        //     if (0 < invalidFiles.Count)
        //     {
        //         if (string.IsNullOrEmpty(postgresql.DownloadUrl))
        //             throw new InvalidDataException("DownloadUrl cannot be null or empty");
        //
        //         var binaryPath = Path.Combine(_executorConfiguration.binaryDirectory,
        //             Path.GetFileName(postgresql.DownloadUrl));
        //
        //         var downloadProgress = new Progress<double>();
        //         downloadProgress.ProgressChanged += (_, value) =>
        //         {
        //             progress?.OnNext(new ProgressInfo
        //             {
        //                 name = $"Downloading {_executorConfiguration.type.ToString()} {postgresql.Version}",
        //                 progress = value / 2.0
        //             });
        //         };
        //         await Download(postgresql.DownloadUrl, binaryPath, downloadProgress);
        //
        //         var extractProgress = new Progress<double>();
        //         extractProgress.ProgressChanged += (_, value) =>
        //         {
        //             progress?.OnNext(new ProgressInfo
        //             {
        //                 name = $"Extracting {Path.GetFileName(postgresql.DownloadUrl)}",
        //                 progress = 0.5 + value / 2.0
        //             });
        //         };
        //         await ExtractZip(binaryPath, extractProgress);
        //     }
        // }
        //
        // #endregion

        #region Search for currently running process and kill it

        var fileName = Path.Combine(_executorConfiguration.binaryDirectory, "galactus.exe");

        progress?.OnNext(new ProgressInfo
        {
            name = $"Checking currently running {_executorConfiguration.type.ToString()}"
        });
        var wmiQueryString =
            $"SELECT ProcessId FROM Win32_Process WHERE ExecutablePath = '{fileName.Replace(@"\", @"\\")}'";
        using (var searcher = new ManagementObjectSearcher(wmiQueryString))
        using (var results = searcher.Get())
        {
            foreach (var result in results)
                try
                {
                    var processId = (uint)result["ProcessId"];
                    var process = Process.GetProcessById((int)processId);

                    process.Kill();
                    await process.WaitForExitAsync();
                }
                catch
                {
                }
        }

        #endregion

        #region Start server

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(_executorConfiguration.binaryDirectory, @"galactus.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _executorConfiguration.binaryDirectory
            }
        };
        foreach (var (key, value) in _executorConfiguration.environmentVariables)
            _process.StartInfo.EnvironmentVariables.Add(key, value);

        _process.Exited += (_, _) => { OnStop(); };

        progress?.OnNext(new ProgressInfo
        {
            name =
                $"Starting {_executorConfiguration.type.ToString()} at port {_executorConfiguration.environmentVariables["GALACTUS_PORT"]}"
        });
        _process.Start();
        progress?.OnCompleted();

        #endregion
    }

    public override Task Stop(ISubject<ProgressInfo>? progress = null)
    {
        if (!IsRunning) return Task.CompletedTask;

        #region Stop server

        progress?.OnNext(new ProgressInfo
        {
            name = $"Stopping {_executorConfiguration.type.ToString()}"
        });
        _process?.Kill();
        _process?.WaitForExit();
        progress?.OnCompleted();
        return Task.CompletedTask;

        #endregion
    }

    public override async Task Restart(ISubject<ProgressInfo>? progress = null)
    {
        #region Stop server

        var stopProgress = new Subject<ProgressInfo>();
        stopProgress.Subscribe(x => progress?.OnNext(new ProgressInfo
        {
            name = x.name,
            progress = x.progress / 2.0
        }));
        await Stop();
        stopProgress.Dispose();

        #endregion

        #region Start server

        var runProgress = new Subject<ProgressInfo>();
        runProgress.Subscribe(x => progress?.OnNext(new ProgressInfo
        {
            name = x.name,
            progress = 0.5 + x.progress / 2.0
        }));
        await Run();
        runProgress.Dispose();
        progress?.OnCompleted();

        #endregion
    }

    public override async Task Install(Dictionary<string, string> parameters,
        Dictionary<ExecutorType, ExecutorControllerBase> executors, ISubject<ProgressInfo>? progress = null)
    {
        #region Retrieve data from PocketBase

        var galactus =
            _pocketBaseClientApplication.Data.GalactusCollection.FirstOrDefault(x =>
                x.Version == _executorConfiguration.binaryVersion);
        if (galactus == null)
            throw new InvalidDataException(
                $"{_executorConfiguration.type.ToString()} {_executorConfiguration.binaryVersion} is not found in the database");
        // TODO: This doesn't work due to a bug of PocketBaseClient-csharp
        // if (galactus.CompatibleExecutors.All(x => x.Version != _executorConfiguration.version))
        //     throw new InvalidDataException(
        //         $"{_executorConfiguration.type.ToString()} {_executorConfiguration.binaryVersion} is not compatible with Executor {_executorConfiguration.version}");
        if (string.IsNullOrEmpty(galactus.DownloadUrl))
            throw new InvalidDataException("DownloadUrl cannot be null or empty");

        #endregion

        #region Download

        if (!Directory.Exists(_executorConfiguration.binaryDirectory))
            Directory.CreateDirectory(_executorConfiguration.binaryDirectory);

        var binaryPath = Path.Combine(_executorConfiguration.binaryDirectory,
            Path.GetFileName(galactus.DownloadUrl));

        var downloadProgress = new Progress<double>();
        downloadProgress.ProgressChanged += (_, value) =>
        {
            progress?.OnNext(new ProgressInfo
            {
                name = $"Downloading {_executorConfiguration.type.ToString()} {galactus.Version}",
                progress = value / 2.0
            });
        };
        await Download(galactus.DownloadUrl, binaryPath, downloadProgress);

        #endregion

        #region Extract

        var extractProgress = new Progress<double>();
        extractProgress.ProgressChanged += (_, value) =>
        {
            progress?.OnNext(new ProgressInfo
            {
                name = $"Extracting {Path.GetFileName(galactus.DownloadUrl)}",
                progress = 0.5 + value / 2.0
            });
        };
        await ExtractZip(binaryPath, extractProgress);

        #endregion
    }

    public override Task Update(Dictionary<string, string> parameters,
        Dictionary<ExecutorType, ExecutorControllerBase> executors, ISubject<ProgressInfo>? progress = null)
    {
        progress?.OnCompleted();
        return Task.CompletedTask;
    }

    public override async Task InstallBySimpleSettings(object simpleSettings, object executorConfigurationBase,
        Dictionary<ExecutorType, ExecutorControllerBase> executors,
        ISubject<ProgressInfo>? progress = null)
    {
        await Install(new Dictionary<string, string>(), executors, progress);
    }

    public override async Task UpdateBySimpleSettings(object simpleSettings, object executorConfigurationBase,
        Dictionary<ExecutorType, ExecutorControllerBase> executors,
        ISubject<ProgressInfo>? progress = null)
    {
        await Update(new Dictionary<string, string>(), executors, progress);
    }

    private Task ExtractZip(string path, IProgress<double>? progress = null)
    {
        using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(Path.GetDirectoryName(path)!, true, progress);
        }

        return Task.CompletedTask;
    }

    private async Task Download(string url, string path, IProgress<double>? progress = null)
    {
        using (var client = new HttpClient())
        using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await client.DownloadDataAsync(url, fileStream, progress);
        }
    }
}