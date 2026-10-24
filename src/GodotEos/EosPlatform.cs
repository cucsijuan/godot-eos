using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Logging;
using Epic.OnlineServices.Platform;

namespace GodotEos;

/// <summary>How the platform starts.</summary>
public sealed class EosPlatformOptions
{
    public string ProductName = "Game";
    public string ProductVersion = "1";

    /// <summary>Where the SDK keeps its cache: a folder of the game's own (in Godot, user://eos globalized).</summary>
    public string CacheDirectory = Path.Combine(Path.GetTempPath(), "eos-cache");

    /// <summary>The SDK's detailed log, not only its warnings.</summary>
    public bool VerboseLog;
}

/// <summary>
/// The Epic Online Services platform, run on a thread of its own: every SDK call happens there, and the game's side gets
/// tasks back. It signs the player in with a device account (no Epic account needed) and keeps the sign-in alive: it
/// lasts an hour, and the platform signs in again before it runs out, retrying a few times over a few minutes.
/// <see cref="EosLobbies"/> and <see cref="EosP2P"/> work on top of it.
/// </summary>
public sealed class EosPlatform : IDisposable
{
    private static readonly int[] RenewalDelays = { 0, 5, 15, 30, 60, 120 };
    private static int _resolverSet, _initialized;

    private readonly EosKeys _keys;
    private readonly EosPlatformOptions _options;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile string? _failure;
    private volatile ProductUserId? _user;
    private Task<string>? _signIn;
    private Task<bool>? _renewal;
    private string _name = "Player";
    private ulong _expiryNotice;

    // The SDK's thread only.
    private PlatformInterface? _platform;

    /// <summary>Diagnostics from the SDK and from here (raised on the SDK's thread).</summary>
    public event Action<string>? Log;

    /// <summary>The player signed in (raised on the SDK's thread, with the platform).</summary>
    internal event Action<PlatformInterface, ProductUserId>? SignedIn;

    /// <summary>Every turn of the SDK's thread, after the platform's tick.</summary>
    internal event Action<PlatformInterface, ProductUserId>? Ticked;

    /// <summary>Before the platform goes: last calls, e.g. leaving lobbies; the platform keeps ticking for a moment after.</summary>
    internal event Action<PlatformInterface, ProductUserId?>? Stopping;

    public EosPlatform(EosKeys keys, EosPlatformOptions? options = null)
    {
        _keys = keys;
        _options = options ?? new EosPlatformOptions();
        _thread = new Thread(Run) { IsBackground = true, Name = "Online services" };
        _thread.Start();
    }

    /// <summary>Why the platform can't be used, once that is known.</summary>
    public string? Failure => _failure;

    public bool IsSignedIn => _user is not null;

    /// <summary>The signed-in player's product user id, as text (what other players address this one by).</summary>
    public string? UserId => _user?.ToString();

    /// <summary>The player's display name, as signed in.</summary>
    public string DisplayName => _name;

    internal ProductUserId? User => _user;

    /// <summary>
    /// Signs in with this computer's device account, made the first time. <paramref name="newDevice"/> replaces it with a
    /// new one first: for testing several players on one computer (those already signed in stay signed in).
    /// </summary>
    public Task<string> SignInWithDeviceAsync(string displayName, bool newDevice = false)
    {
        lock (_work)
        {
            if (_signIn is { IsFaulted: false, IsCanceled: false })
                return _signIn;
            _name = displayName.Length > 32 ? displayName[..32] : displayName.Length == 0 ? "Player" : displayName;
            return _signIn = Run<string>((platform, done) => CreateDevice(platform, newDevice, done));
        }
    }

    /// <summary>Runs <paramref name="work"/> on the SDK's thread; it completes the task (the SDK answers by callbacks).</summary>
    internal Task<T> Run<T>(Action<PlatformInterface, TaskCompletionSource<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            if (_platform is null)
                done.TrySetException(new InvalidOperationException(_failure ?? "Online services aren't running."));
            else
            {
                try { work(_platform, done); }
                catch (Exception e) { done.TrySetException(e); }
            }
        });
        return done.Task;
    }

    /// <summary>Runs <paramref name="work"/> on the SDK's thread, if the platform is up.</summary>
    internal void Post(Action<PlatformInterface> work) => _work.Enqueue(() =>
    {
        if (_platform is not null)
            work(_platform);
    });

    internal void Say(string what) => Log?.Invoke(what);

    internal static void Fail<T>(TaskCompletionSource<T> done, string what, Result result) =>
        done.TrySetException(new EosException($"Online services failed {what}: {result}.", result));

    /// <summary>Signs in again with the same device account (the SDK reports the sign-in running out, or a call found it expired).</summary>
    internal Task<bool> SignInAgainAsync()
    {
        lock (_work)
        {
            if (_renewal is { IsCompleted: false } running)
                return running;
            return _renewal = RenewAsync();
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(3));
    }

    // ------------------------------------------------------------------ the SDK's thread

    private void Run()
    {
        try { Start(); }
        catch (Exception e) { _failure = $"Online services couldn't start: {e.Message}"; }
        while (!_stop)
        {
            while (_work.TryDequeue(out var work))
                work();
            if (_platform is not null)
            {
                _platform.Tick();
                if (_user is { } user)
                    Ticked?.Invoke(_platform, user);
            }
            Thread.Sleep(_work.IsEmpty ? 4 : 0);
        }
        Stop();
    }

    /// <summary>
    /// The SDK's native library sits next to its assembly (copied there by the build and the export); under Godot the
    /// runtime doesn't look there by itself.
    /// </summary>
    private static void FindNativeLibrary()
    {
        if (Interlocked.Exchange(ref _resolverSet, 1) == 1)
            return;
        NativeLibrary.SetDllImportResolver(typeof(Common).Assembly, (name, assembly, path) =>
        {
            if (!name.Contains("EOSSDK"))
                return IntPtr.Zero;
            var places = new[] { Path.GetDirectoryName(assembly.Location), AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) };
            foreach (var place in places.Where(p => !string.IsNullOrEmpty(p)))
            {
                string file = Path.Combine(place!, name);
                foreach (string candidate in new[] { file, file + ".so", file + ".dll" })
                    if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                        return handle;
            }
            return IntPtr.Zero;
        });
    }

    private void Start()
    {
        if (!_keys.IsComplete)
            throw new InvalidOperationException("the product's keys are missing");
        FindNativeLibrary();
        LoggingInterface.SetLogLevel(LogCategory.AllCategories, _options.VerboseLog ? LogLevel.Verbose : LogLevel.Warning);
        LoggingInterface.SetCallback((ref LogMessage message) => Log?.Invoke($"EOS {message.Category}: {message.Message}"));
        if (Interlocked.Increment(ref _initialized) == 1)
        {
            var init = new InitializeOptions { ProductName = _options.ProductName, ProductVersion = _options.ProductVersion };
            var result = PlatformInterface.Initialize(ref init);
            if (result != Result.Success && result != Result.AlreadyConfigured)
                throw new InvalidOperationException(result.ToString());
        }
        Directory.CreateDirectory(_options.CacheDirectory);
        var options = new Options
        {
            ProductId = _keys.ProductId,
            SandboxId = _keys.SandboxId,
            DeploymentId = _keys.DeploymentId,
            ClientCredentials = new ClientCredentials { ClientId = _keys.ClientId, ClientSecret = _keys.ClientSecret },
            IsServer = false,
            CacheDirectory = _options.CacheDirectory,
            Flags = PlatformFlags.DisableOverlay,
        };
        if (_keys.EncryptionKey.Length == 64)
            options.EncryptionKey = _keys.EncryptionKey;
        _platform = PlatformInterface.Create(ref options)
            ?? throw new InvalidOperationException("the platform couldn't be created (check the product's keys)");
    }

    private void Stop()
    {
        if (_platform is null)
            return;
        try { Stopping?.Invoke(_platform, _user); }
        catch (Exception e) { Log?.Invoke($"Online services, stopping: {e.Message}"); }
        for (int i = 0; i < 50; i++)                    // let the last calls go out (leaving lobbies, closing connections)
        {
            while (_work.TryDequeue(out var work))
                work();
            _platform.Tick();
            Thread.Sleep(10);
        }
        if (_expiryNotice != 0)
            _platform.GetConnectInterface().RemoveNotifyAuthExpiration(_expiryNotice);
        _platform.Release();
        _platform = null;
        _user = null;
        if (Interlocked.Decrement(ref _initialized) == 0)
            PlatformInterface.Shutdown();
    }

    // ------------------------------------------------------------------ signing in

    private void CreateDevice(PlatformInterface platform, bool replace, TaskCompletionSource<string> done)
    {
        var connect = platform.GetConnectInterface();
        if (replace)
        {
            var delete = new DeleteDeviceIdOptions();
            connect.DeleteDeviceId(ref delete, null, (ref DeleteDeviceIdCallbackInfo _) => CreateDevice(platform, false, done));
            return;
        }
        var create = new CreateDeviceIdOptions { DeviceModel = Environment.MachineName };
        connect.CreateDeviceId(ref create, null, (ref CreateDeviceIdCallbackInfo info) =>
        {
            if (info.ResultCode is Result.Success or Result.DuplicateNotAllowed)
                Login(platform, done);
            else
                Fail(done, "making the device account", info.ResultCode);
        });
    }

    private void Login(PlatformInterface platform, TaskCompletionSource<string> done)
    {
        var connect = platform.GetConnectInterface();
        var login = new LoginOptions
        {
            Credentials = new Credentials { Type = ExternalCredentialType.DeviceidAccessToken, Token = null },
            UserLoginInfo = new UserLoginInfo { DisplayName = _name },
        };
        connect.Login(ref login, null, (ref LoginCallbackInfo info) =>
        {
            if (info.ResultCode == Result.Success)
                Done(platform, info.LocalUserId, done);
            else if (info.ResultCode == Result.InvalidUser)
            {
                // A device account the product hasn't seen: make its player.
                var create = new CreateUserOptions { ContinuanceToken = info.ContinuanceToken };
                connect.CreateUser(ref create, null, (ref CreateUserCallbackInfo created) =>
                {
                    if (created.ResultCode == Result.Success)
                        Done(platform, created.LocalUserId, done);
                    else
                        Fail(done, "making the player", created.ResultCode);
                });
            }
            else
                Fail(done, "signing in", info.ResultCode);
        });
    }

    private void Done(PlatformInterface platform, ProductUserId user, TaskCompletionSource<string> done)
    {
        bool first = _user is null;
        _user = user;
        if (_expiryNotice == 0)
        {
            var notify = new AddNotifyAuthExpirationOptions();
            _expiryNotice = platform.GetConnectInterface().AddNotifyAuthExpiration(ref notify, null,
                (ref AuthExpirationCallbackInfo info) => { _ = SignInAgainAsync(); });
        }
        if (first)
            SignedIn?.Invoke(platform, user);
        done.TrySetResult(user.ToString());
    }

    private async Task<bool> RenewAsync()
    {
        for (int attempt = 0; attempt < RenewalDelays.Length; attempt++)
        {
            if (RenewalDelays[attempt] > 0)
                await Task.Delay(TimeSpan.FromSeconds(RenewalDelays[attempt]));
            try
            {
                await Run<string>(Login);
                if (attempt > 0)
                    Log?.Invoke($"Online services: sign-in renewed (attempt {attempt + 1}).");
                Renewed?.Invoke();
                return true;
            }
            catch (Exception e)
            {
                Log?.Invoke($"Online services couldn't renew the sign-in (attempt {attempt + 1} of {RenewalDelays.Length}): {e.Message}");
            }
        }
        return false;
    }

    /// <summary>The sign-in was renewed (raised on the SDK's thread).</summary>
    internal event Action? Renewed;
}

/// <summary>An SDK call that failed, with the SDK's result.</summary>
public sealed class EosException : InvalidOperationException
{
    public Result Result { get; }

    public EosException(string message, Result result) : base(message) { Result = result; }
}
