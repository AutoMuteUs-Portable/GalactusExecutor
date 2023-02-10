using System.Diagnostics;
using System.Management;
using System.Reactive.Subjects;
using AutoMuteUsPortable.PocketBaseClient;
using AutoMuteUsPortable.Shared.Controller.Executor;
using AutoMuteUsPortable.Shared.Entity.ExecutorConfigurationBaseNS;
using AutoMuteUsPortable.Shared.Entity.ExecutorConfigurationNS;
using AutoMuteUsPortable.Shared.Entity.ProgressInfo;
using AutoMuteUsPortable.Shared.Utility;
using FluentValidation;

namespace AutoMuteUsPortable.Executor;

public class ExecutorController : ExecutorControllerBase
{
    private readonly PocketBaseClientApplication _pocketBaseClientApplication = new();
    private Process? _process;
    private readonly StreamWriter _outputStreamWriter;
    private readonly StreamWriter _errorStreamWriter;

    public ExecutorController(object executorConfiguration) : base(executorConfiguration)
    {
        #region Initialize stream writer

        _outputStreamWriter = new StreamWriter(OutputStream);
        _errorStreamWriter = new StreamWriter(ErrorStream);

        #endregion

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

        ExecutorConfiguration = tmp;

        #endregion
    }

    public ExecutorController(object computedSimpleSettings,
        object executorConfigurationBase) : base(computedSimpleSettings, executorConfigurationBase)
    {
        #region Initialize stream writer

        _outputStreamWriter = new StreamWriter(OutputStream);
        _errorStreamWriter = new StreamWriter(ErrorStream);

        #endregion

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

        ExecutorConfiguration = executorConfiguration;

        #endregion
    }

    public override async Task Run(ISubject<ProgressInfo>? progress = null)
    {
        if (IsRunning) return;

        #region Setup progress

        var taskProgress = progress != null
            ? new TaskProgress(progress, new Dictionary<string, object?>
            {
                {
                    "File integrity check", new List<string>
                    {
                        "Checking file integrity",
                        "Downloading",
                        "Extracting"
                    }
                },
                {
                    "Killing currently running server",
                    null
                },
                {
                    "Starting server",
                    null
                }
            })
            : null;

        #endregion

        #region Retrieve data from PocketBase

        var galactus =
            _pocketBaseClientApplication.Data.GalactusCollection.FirstOrDefault(x =>
                x.Version == ExecutorConfiguration.binaryVersion);
        if (galactus == null)
            throw new InvalidDataException(
                $"{ExecutorConfiguration.type} {ExecutorConfiguration.binaryVersion} is not found in the database");
        // TODO: This doesn't work due to a bug of PocketBaseClient-csharp
        // if (galactus.CompatibleExecutors.All(x => x.Version != _executorConfiguration.version))
        //     throw new InvalidDataException(
        //         $"{_executorConfiguration.type} {_executorConfiguration.binaryVersion} is not compatible with Executor {_executorConfiguration.version}");

        #endregion

        #region Check file integrity

        using (var client = new HttpClient())
        {
            var checksumUrl = Utils.GetChecksum(galactus.Checksum);
            var res = await client.GetStringAsync(checksumUrl);
            var checksum = Utils.ParseChecksumText(res);
            var checksumProgress = taskProgress?.GetSubjectProgress();
            checksumProgress?.OnNext(new ProgressInfo
            {
                name = string.Format("{0}のファイルの整合性を確認しています", ExecutorConfiguration.type),
                IsIndeterminate = true
            });
            var invalidFiles = Utils.CompareChecksum(ExecutorConfiguration.binaryDirectory, checksum);
            taskProgress?.NextTask();

            if (0 < invalidFiles.Count)
            {
                var downloadUrl = Utils.GetDownloadUrl(galactus.DownloadUrl);
                if (string.IsNullOrEmpty(downloadUrl))
                    throw new InvalidDataException("DownloadUrl cannot be null or empty");

                var binaryPath = Path.Combine(ExecutorConfiguration.binaryDirectory,
                    Path.GetFileName(downloadUrl));

                var downloadProgress = taskProgress?.GetProgress();
                if (taskProgress?.ActiveLeafTask != null)
                    taskProgress.ActiveLeafTask.Name =
                        string.Format("{0}の実行に必要なファイルをダウンロードしています", ExecutorConfiguration.type);
                await Utils.DownloadAsync(downloadUrl, binaryPath, downloadProgress);
                taskProgress?.NextTask();

                var extractProgress = taskProgress?.GetProgress();
                if (taskProgress?.ActiveLeafTask != null)
                    taskProgress.ActiveLeafTask.Name =
                        string.Format("{0}の実行に必要なファイルを解凍しています", ExecutorConfiguration.type);
                Utils.ExtractZip(binaryPath, extractProgress);
                taskProgress?.NextTask();
            }
            else
            {
                taskProgress?.NextTask(2);
            }
        }

        #endregion

        #region Search for currently running process and kill it

        var fileName = Path.Combine(ExecutorConfiguration.binaryDirectory, "galactus.exe");

        var killingProgress = taskProgress?.GetSubjectProgress();
        killingProgress?.OnNext(new ProgressInfo
        {
            name = string.Format("既に起動している{0}を終了しています", ExecutorConfiguration.type),
            IsIndeterminate = true
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

        taskProgress?.NextTask();

        #endregion

        #region Start server

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(ExecutorConfiguration.binaryDirectory, @"galactus.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = ExecutorConfiguration.binaryDirectory
            }
        };
        foreach (var (key, value) in ExecutorConfiguration.environmentVariables)
            _process.StartInfo.EnvironmentVariables.Add(key, value);

        OnStart();
        _process.Exited += (_, _) => { OnStop(); };

        var startProgress = taskProgress?.GetSubjectProgress();
        startProgress?.OnNext(new ProgressInfo
        {
            name = string.Format("{0}を起動しています", ExecutorConfiguration.type),
            IsIndeterminate = true
        });
        _process.Start();

        _process.OutputDataReceived += ProcessOnOutputDataReceived;
        _process.BeginOutputReadLine();

        _process.ErrorDataReceived += ProcessOnErrorDataReceived;
        _process.BeginErrorReadLine();

        taskProgress?.NextTask();

        #endregion
    }

    public override Task Stop(ISubject<ProgressInfo>? progress = null)
    {
        if (!IsRunning) return Task.CompletedTask;

        #region Stop server

        progress?.OnNext(new ProgressInfo
        {
            name = string.Format("{0}を終了しています", ExecutorConfiguration.type),
            IsIndeterminate = true
        });
        _process?.Kill();
        _process?.WaitForExit();
        return Task.CompletedTask;

        #endregion
    }

    public override async Task Restart(ISubject<ProgressInfo>? progress = null)
    {
        if (!IsRunning) return;

        #region Setup progress

        var taskProgress = progress != null
            ? new TaskProgress(progress, new List<string>
            {
                "Stopping",
                "Starting"
            })
            : null;

        #endregion

        #region Stop server

        var stopProgress = taskProgress?.GetSubjectProgress();
        await Stop(stopProgress);
        taskProgress?.NextTask();

        #endregion

        #region Start server

        var runProgress = taskProgress?.GetSubjectProgress();
        await Run(runProgress);
        taskProgress?.NextTask();

        #endregion
    }

    public override async Task Install(
        Dictionary<ExecutorType, ExecutorControllerBase> executors, ISubject<ProgressInfo>? progress = null)
    {
        #region Setup progress

        var taskProgress = progress != null
            ? new TaskProgress(progress, new List<string>
            {
                "Downloading",
                "Extracting"
            })
            : null;

        #endregion

        #region Retrieve data from PocketBase

        var galactus =
            _pocketBaseClientApplication.Data.GalactusCollection.FirstOrDefault(x =>
                x.Version == ExecutorConfiguration.binaryVersion);
        if (galactus == null)
            throw new InvalidDataException(
                $"{ExecutorConfiguration.type} {ExecutorConfiguration.binaryVersion} is not found in the database");
        if (galactus.CompatibleExecutors.All(x => x.Version != ExecutorConfiguration.version))
            throw new InvalidDataException(
                $"{ExecutorConfiguration.type} {ExecutorConfiguration.binaryVersion} is not compatible with Executor {ExecutorConfiguration.version}");
        var downloadUrl = Utils.GetDownloadUrl(galactus.DownloadUrl);
        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidDataException("DownloadUrl cannot be null or empty");

        #endregion

        #region Download

        if (!Directory.Exists(ExecutorConfiguration.binaryDirectory))
            Directory.CreateDirectory(ExecutorConfiguration.binaryDirectory);

        var binaryPath = Path.Combine(ExecutorConfiguration.binaryDirectory,
            Path.GetFileName(downloadUrl));

        var downloadProgress = taskProgress?.GetProgress();
        if (taskProgress?.ActiveLeafTask != null)
            taskProgress.ActiveLeafTask.Name = string.Format("{0}の実行に必要なファイルをダウンロードしています", ExecutorConfiguration.type);
        await Utils.DownloadAsync(downloadUrl, binaryPath, downloadProgress);
        taskProgress?.NextTask();

        #endregion

        #region Extract

        var extractProgress = taskProgress?.GetProgress();
        if (taskProgress?.ActiveLeafTask != null)
            taskProgress.ActiveLeafTask.Name = string.Format("{0}の実行に必要なファイルを解凍しています", ExecutorConfiguration.type);
        Utils.ExtractZip(binaryPath, extractProgress);
        taskProgress?.NextTask();

        #endregion
    }

    public override Task Update(
        Dictionary<ExecutorType, ExecutorControllerBase> executors, object oldExecutorConfiguration,
        ISubject<ProgressInfo>? progress = null)
    {
        return Task.CompletedTask;
    }

    private void ProcessOnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        _outputStreamWriter.Write(e.Data);
    }

    private void ProcessOnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        _errorStreamWriter.Write(e.Data);
    }
}