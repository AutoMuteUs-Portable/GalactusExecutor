using System.Diagnostics;
using System.Management;
using System.Reactive.Subjects;
using System.Text;
using AutoMuteUsPortable.PocketBaseClient;
using AutoMuteUsPortable.Shared.Controller.Executor;
using AutoMuteUsPortable.Shared.Entity.ExecutorConfigurationBaseNS;
using AutoMuteUsPortable.Shared.Entity.ExecutorConfigurationNS;
using AutoMuteUsPortable.Shared.Entity.ProgressInfo;
using AutoMuteUsPortable.Shared.Utility;
using CliWrap;
using CliWrap.EventStream;
using FluentValidation;
using Serilog;

namespace AutoMuteUsPortable.Executor;

public class ExecutorController : ExecutorControllerBase
{
    private readonly PocketBaseClientApplication _pocketBaseClientApplication = new();
    private CancellationTokenSource _forcefulCTS = new();
    private CancellationTokenSource _gracefulCTS = new();

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

        ExecutorConfiguration = tmp;

        Log.Debug("ExecutorController is instantiated with {@ExecutorConfiguration}", ExecutorConfiguration);

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
                ["DISCORD_BOT_TOKEN"] = discordToken,
                ["REDIS_ADDR"] = $"localhost:{redisPort}",
                ["GALACTUS_PORT"] = galactusPort.ToString() ?? "",
                ["BROKER_PORT"] = brokerPort.ToString() ?? "",
                ["REDIS_USER"] = "",
                ["REDIS_PASS"] = ""
            }
        };

        var validator = new ExecutorConfigurationValidator();
        validator.ValidateAndThrow(executorConfiguration);

        ExecutorConfiguration = executorConfiguration;

        Log.Debug("ExecutorController is instantiated with {@ExecutorConfiguration}", ExecutorConfiguration);

        #endregion
    }

    public override async Task Run(ISubject<ProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;

        #region Setup progress

        var taskProgress = progress != null
            ? new TaskProgress(progress, new Dictionary<string, object?>
            {
                ["File integrity check"] = new List<string>
                {
                    "Checking file integrity",
                    "Downloading",
                    "Extracting"
                },
                ["Killing currently running server"] = null,
                ["Starting server"] = null
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

        #endregion

        #region Check file integrity

        var checksumUrl = Utils.GetChecksum(galactus.Checksum);

        if (string.IsNullOrEmpty(checksumUrl))
        {
#if DEBUG
            Log.Debug("Checksum is null or empty, skipping integrity check");
            taskProgress?.NextTask(3);
#else
                throw new InvalidDataException("Checksum cannot be null or empty");
#endif
        }
        else
        {
            using (var client = new HttpClient())
            {
                var res = await client.GetStringAsync(checksumUrl, cancellationToken);
                var checksum = Utils.ParseChecksumText(res);
                var checksumProgress = taskProgress?.GetSubjectProgress();
                checksumProgress?.OnNext(new ProgressInfo
                {
                    name = string.Format("{0}のファイルの整合性を確認しています", ExecutorConfiguration.type),
                    IsIndeterminate = true
                });
                var invalidFiles =
                    Utils.CompareChecksum(ExecutorConfiguration.binaryDirectory, checksum, cancellationToken);
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
                    await Utils.DownloadAsync(downloadUrl, binaryPath, downloadProgress, cancellationToken);
                    taskProgress?.NextTask();

                    var extractProgress = taskProgress?.GetProgress();
                    if (taskProgress?.ActiveLeafTask != null)
                        taskProgress.ActiveLeafTask.Name =
                            string.Format("{0}の実行に必要なファイルを解凍しています", ExecutorConfiguration.type);
                    Utils.ExtractZip(binaryPath, extractProgress, cancellationToken);
                    taskProgress?.NextTask();
                }
                else
                {
                    taskProgress?.NextTask(2);
                }
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
            {
                try
                {
                    Log.Debug("Killing already running process {ProcessId}", result["ProcessId"]);
                    var processId = (uint)result["ProcessId"];
                    var process = Process.GetProcessById((int)processId);

                    process.Kill();
                }
                catch
                {
                    // ignored
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        taskProgress?.NextTask();

        #endregion

        #region Start server

        var startProgress = taskProgress?.GetSubjectProgress();
        startProgress?.OnNext(new ProgressInfo
        {
            name = string.Format("{0}を起動しています", ExecutorConfiguration.type),
            IsIndeterminate = true
        });
        var cmd = Cli.Wrap(Path.Combine(ExecutorConfiguration.binaryDirectory, @"galactus.exe"))
            .WithEnvironmentVariables(ExecutorConfiguration.environmentVariables!)
            .WithWorkingDirectory(ExecutorConfiguration.binaryDirectory)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(ProcessStandardOutput, Encoding.UTF8))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(ProcessStandardError, Encoding.UTF8))
            .WithValidation(CommandResultValidation.None);

        _forcefulCTS = new CancellationTokenSource();
        _gracefulCTS = new CancellationTokenSource();
        cancellationToken.Register(() => ForciblyStop());
        try
        {
            cmd.Observe(Encoding.UTF8, Encoding.UTF8, _forcefulCTS.Token, _gracefulCTS.Token)
                .Subscribe(
                    e =>
                    {
                        if (e is StartedCommandEvent started) OnStart();
                    }, _ => OnStop(), OnStop);
        }
        catch (OperationCanceledException ex)
        {
            OnStop();
        }

        taskProgress?.NextTask();

        #endregion
    }

    public override Task GracefullyStop(ISubject<ProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning) return Task.CompletedTask;

        Log.Debug("Gracefully stopping {Type}", ExecutorConfiguration.type);

        #region Stop server

        var ewh = new ManualResetEvent(false);
        Stopped += (sender, args) => ewh.Set();

        progress?.OnNext(new ProgressInfo
        {
            name = string.Format("{0}を終了しています", ExecutorConfiguration.type),
            IsIndeterminate = true
        });
        _gracefulCTS.Cancel();
        if (WaitHandle.WaitAny(new[] { ewh, cancellationToken.WaitHandle }) != 0)
            cancellationToken.ThrowIfCancellationRequested();

        return Task.CompletedTask;

        #endregion
    }

    public override Task ForciblyStop(ISubject<ProgressInfo>? progress = null)
    {
        if (!IsRunning) return Task.CompletedTask;

        Log.Debug("Forcibly stopping {Type}", ExecutorConfiguration.type);

        #region Stop server

        var ewh = new ManualResetEvent(false);
        Stopped += (sender, args) => ewh.Set();

        progress?.OnNext(new ProgressInfo
        {
            name = string.Format("{0}を終了しています", ExecutorConfiguration.type),
            IsIndeterminate = true
        });
        _forcefulCTS.Cancel();
        ewh.WaitOne();

        return Task.CompletedTask;

        #endregion
    }

    public override async Task Restart(ISubject<ProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
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
        await GracefullyStop(stopProgress, cancellationToken);
        taskProgress?.NextTask();

        #endregion

        #region Start server

        var runProgress = taskProgress?.GetSubjectProgress();
        await Run(runProgress, cancellationToken);
        taskProgress?.NextTask();

        #endregion
    }

    public override async Task Install(
        Dictionary<ExecutorType, ExecutorControllerBase> executors, ISubject<ProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
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
        await Utils.DownloadAsync(downloadUrl, binaryPath, downloadProgress, cancellationToken);
        taskProgress?.NextTask();

        #endregion

        #region Extract

        var extractProgress = taskProgress?.GetProgress();
        if (taskProgress?.ActiveLeafTask != null)
            taskProgress.ActiveLeafTask.Name = string.Format("{0}の実行に必要なファイルを解凍しています", ExecutorConfiguration.type);
        Utils.ExtractZip(binaryPath, extractProgress, cancellationToken);
        taskProgress?.NextTask();

        #endregion
    }

    public override Task Update(
        Dictionary<ExecutorType, ExecutorControllerBase> executors, object oldExecutorConfiguration,
        ISubject<ProgressInfo>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    private void ProcessStandardOutput(string text)
    {
        Log.Verbose("[{ExecutorType}] {Text}", ExecutorConfiguration.type, text);

        StandardOutput.OnNext(text);
    }

    private void ProcessStandardError(string text)
    {
        Log.Verbose("[{ExecutorType}] {Text}", ExecutorConfiguration.type, text);

        StandardError.OnNext(text);
    }
}