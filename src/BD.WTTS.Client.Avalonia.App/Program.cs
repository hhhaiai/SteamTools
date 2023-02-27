using static BD.WTTS.CommandLineHost;
#if WINDOWS
using AppResources = BD.WTTS.Client.Resources.Strings;
#endif

namespace BD.WTTS;

static partial class Program
{
#if WINDOWS
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ShowErrMessageBox(string error) => WPFMessageBox.Show(error, AppResources.Error, WPFMessageBoxButton.OK, WPFMessageBoxImage.Error);
#endif

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
#if WINDOWS
        // 兼容性检查
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            ShowErrMessageBox("此应用程序仅兼容 Windows 11 与 Windows 10 版本 1809（OS 内部版本 17763）或更高版本");
            return 0;
        }
        if (AppContext.BaseDirectory.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
        {
            // 检测当前目录 Temp\Rar$ 这类目录，可能是在压缩包中直接启动程序导致的，还有一堆 文件找不到/加载失败的异常
            //  System.IO.DirectoryNotFoundException: Could not find a part of the path 'C:\Users\USER\AppData\Local\Temp\Rar$EXa15528.13350\Cache\switchproxy.reg'.
            //  System.IO.FileLoadException ...
            //  System.IO.FileNotFoundException: Could not load file or assembly ...
            ShowErrMessageBox(AppResources.Error_BaseDir_StartsWith_Temp);
            return 0;
        }
#endif

#if DEBUG
        // 调试时移动本机库到 native，通常指定了单个 RID(RuntimeIdentifier) 后本机库将位于程序根目录上否则将位于 runtimes 文件夹中
        GlobalDllImportResolver.MoveFiles();
#endif

        // 监听当前应用程序域的程序集加载
        AppDomain.CurrentDomain.AssemblyLoad += CurrentDomain_AssemblyLoad;
        static void CurrentDomain_AssemblyLoad(object? sender, AssemblyLoadEventArgs args)
        {
#if DEBUG
            Console.WriteLine($"loadasm: {args.LoadedAssembly}");
#endif
            // 使用 native 文件夹导入解析本机库
            try
            {
                NativeLibrary.SetDllImportResolver(args.LoadedAssembly, GlobalDllImportResolver.Delegate);
            }
            catch
            {
                // ArgumentNullException assembly 或 resolver 为 null。
                // ArgumentException 已为此程序集设置解析程序。
                // 此每程序集解析程序是第一次尝试解析此程序集启动的本机库加载。
                // 此方法的调用方应仅为自己的程序集注册解析程序。
                // 每个程序集只能注册一个解析程序。 尝试注册第二个解析程序失败并出现 InvalidOperationException。
                // https://learn.microsoft.com/zh-cn/dotnet/api/system.runtime.interopservices.nativelibrary.setdllimportresolver
            }
        }

        // 注册 MemoryPack 某些自定义类型的格式化，如 Cookie, IPAddress, RSAParameters
        MemoryPackFormatterProvider.Register<MemoryPackFormatters>();

        // 添加 .NET Framework 中可用的代码页提供对编码提供程序
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // fix The request was aborted: Could not create SSL/TLS secure channel
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

#if WINDOWS
        // 在 Windows 上还原 .NET Framework 中网络请求跟随系统网络代理变化而动态切换代理行为
        HttpClient.DefaultProxy = DynamicHttpWindowsProxy.Instance;
#endif
#if WINDOWS_DESKTOP_BRIDGE
        if (!DesktopBridgeHelper.Init()) return 0;
        InitWithDesktopBridge(ref args);
#elif IOS || MACOS || MACCATALYST
        UIApplication.Main(args, null, typeof(AppDelegate));
#endif

        var host = Host.Instance;
        host.IsMainProcess = args.Length == 0;
        host.IsCLTProcess = !host.IsMainProcess && args.FirstOrDefault() == "-clt";

        OnCreateAppExecuting();

        try
        {
            string[] args_clt;
            if (host.IsCLTProcess) // 命令行模式
            {
                args_clt = args.Skip(1).ToArray();
                if (args_clt.Length == 1 && args_clt[0].Equals(command_main, StringComparison.OrdinalIgnoreCase)) return default;
            }
            else
            {
                args_clt = new[] { command_main };
            }
            return host.Run(args_clt);
        }
        catch (Exception ex)
        {
            GlobalExceptionHelpers.Handler(ex, nameof(Main));
            throw;
        }
        finally
        {
            // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
            PlatformApp?.Dispose();
            Ioc.Dispose();
            LogManager.Shutdown();
        }
    }

#if WINDOWS_DESKTOP_BRIDGE
    static void InitWithDesktopBridge(ref string[] args)
    {
        DesktopBridgeHelper.OnActivated(ref args);
    }
#endif

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        // 设计器模式不会执行 Main 函数所以以此区分来初始化文件系统
        if (Design.IsDesignMode)
        {
            OnCreateAppExecuting();
        }

        var builder = AppBuilder.Configure(() => Host.Instance.App)
               .UsePlatformDetect()
               .LogToTrace()
               .UseReactiveUI();

        var useGpu = !IApplication.DisableGPU && GeneralSettings.UseGPURendering.Value;
#if MACOS
        builder.With(new AvaloniaNativePlatformOptions
        {
            UseGpu = useGpu,
        });
#elif LINUX
        builder.With(new X11PlatformOptions
        {
            UseGpu = useGpu,
        });
#elif WINDOWS
        var useWgl = IApplication.UseWgl || GeneralSettings.UseWgl.Value;
        var options = new Win32PlatformOptions
        {
            UseWgl = useWgl,
            AllowEglInitialization = useGpu,
        };
        builder.With(options);

        var skiaOptions = new SkiaOptions
        {
            MaxGpuResourceSizeBytes = 1024000000,
        };

        builder.With(skiaOptions);
#else
        throw new PlatformNotSupportedException();
#endif
        return builder;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void StartAvaloniaApp(string[] args,
        ShutdownMode shutdownMode = ShutdownMode.OnLastWindowClose)
    {
        if (!Environment.UserInteractive) return;
        var builder = BuildAvaloniaApp();
        builder.StartWithClassicDesktopLifetime2(args, shutdownMode);
    }
}
