using System;
using System.Runtime.InteropServices;

namespace Loupedeck.MacroClaudePlugin.Focus;

// Minimal Objective-C runtime bridge for raising a macOS application
// window to the foreground without pulling in net8.0-macos workloads or
// shelling out to `osascript`.
//
// It only exposes ActivateByBundleId(string) which maps to:
//
//   NSArray *apps = [NSRunningApplication
//       runningApplicationsWithBundleIdentifier:@"<bundle>"];
//   if (apps.count > 0) {
//       [[apps firstObject]
//           activateWithOptions:NSApplicationActivateIgnoringOtherApps];
//   }
//
// Calling convention: ARM64 x0 holds integer, pointer, and bool returns
// for objc_msgSend, so IntPtr is a safe universal return type we can
// cast from.
//
// Framework loading: libobjc.A.dylib only contains the Objective-C
// runtime itself. Classes like NSString and NSRunningApplication live
// in Foundation.framework and AppKit.framework respectively, which are
// NOT auto-loaded into a pure console .NET host like Logi Plugin
// Service. Without an explicit dlopen, objc_getClass("NSRunningApplication")
// returns NULL and every call to this class silently returns false.
// We force-load both frameworks on the first call via a static ctor.
internal static class NativeActivator
{
    private const String LibObjc = "/usr/lib/libobjc.A.dylib";
    private const String LibDl = "/usr/lib/libSystem.B.dylib";
    private const String FoundationPath = "/System/Library/Frameworks/Foundation.framework/Foundation";
    private const String AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private const Int32 RtldNow = 2;

    // NSApplicationActivateIgnoringOtherApps = 1 << 1 = 2
    private const UInt64 NSApplicationActivateIgnoringOtherApps = 2;

    private static readonly Object FrameworkLoadLock = new();
    private static Boolean _frameworksLoaded;

    [DllImport(LibDl, EntryPoint = "dlopen")]
    private static extern IntPtr DlOpen([MarshalAs(UnmanagedType.LPStr)] String path, Int32 mode);

    [DllImport(LibObjc, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPStr)] String name);

    [DllImport(LibObjc, EntryPoint = "sel_registerName")]
    private static extern IntPtr RegisterSel([MarshalAs(UnmanagedType.LPStr)] String name);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr target, IntPtr sel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr target, IntPtr sel, IntPtr arg1);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendStr(
        IntPtr target,
        IntPtr sel,
        [MarshalAs(UnmanagedType.LPStr)] String arg1);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendUInt64(IntPtr target, IntPtr sel, UInt64 arg1);

    public static Boolean ActivateByBundleId(String bundleId)
    {
        if (String.IsNullOrWhiteSpace(bundleId))
        {
            PluginLog.Warning("macro-claude: ActivateByBundleId called with empty bundle id");
            return false;
        }

        if (!TryLoadFrameworks())
        {
            return false;
        }

        try
        {
            var nsStringCls = GetClass("NSString");
            if (nsStringCls == IntPtr.Zero)
            {
                PluginLog.Error("macro-claude: objc_getClass(NSString) returned NULL — Foundation.framework not loaded");
                return false;
            }

            // [NSString stringWithUTF8String:bundleId] — class method,
            // avoids the alloc/init dance.
            var selStringUtf8 = RegisterSel("stringWithUTF8String:");
            var nsBundleId = MsgSendStr(nsStringCls, selStringUtf8, bundleId);
            if (nsBundleId == IntPtr.Zero)
            {
                PluginLog.Error($"macro-claude: NSString stringWithUTF8String: returned NULL for '{bundleId}'");
                return false;
            }

            var runningAppCls = GetClass("NSRunningApplication");
            if (runningAppCls == IntPtr.Zero)
            {
                PluginLog.Error("macro-claude: objc_getClass(NSRunningApplication) returned NULL — AppKit.framework not loaded");
                return false;
            }

            var selRunningApps = RegisterSel("runningApplicationsWithBundleIdentifier:");
            var apps = MsgSend(runningAppCls, selRunningApps, nsBundleId);
            if (apps == IntPtr.Zero)
            {
                PluginLog.Warning($"macro-claude: runningApplicationsWithBundleIdentifier: returned NULL for {bundleId}");
                return false;
            }

            // [apps count] — NSUInteger returned in x0, zero means the
            // target app is not currently running.
            var selCount = RegisterSel("count");
            var count = (UInt64)MsgSend(apps, selCount).ToInt64();
            if (count == 0)
            {
                PluginLog.Info($"macro-claude: no running application matches bundle id '{bundleId}'");
                return false;
            }

            var selFirst = RegisterSel("firstObject");
            var firstApp = MsgSend(apps, selFirst);
            if (firstApp == IntPtr.Zero)
            {
                PluginLog.Warning("macro-claude: NSArray firstObject returned NULL despite count > 0");
                return false;
            }

            // [firstApp activateWithOptions:2] — BOOL return, 0 or 1.
            var selActivate = RegisterSel("activateWithOptions:");
            var result = MsgSendUInt64(firstApp, selActivate, NSApplicationActivateIgnoringOtherApps);
            var activated = result != IntPtr.Zero;
            PluginLog.Info($"macro-claude: activateWithOptions: returned {(activated ? "YES" : "NO")} for {bundleId}");
            return activated;
        }
        catch (DllNotFoundException ex)
        {
            PluginLog.Error(ex, "macro-claude: DllNotFoundException in NativeActivator");
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            PluginLog.Error(ex, "macro-claude: EntryPointNotFoundException in NativeActivator");
            return false;
        }
    }

    private static Boolean TryLoadFrameworks()
    {
        if (_frameworksLoaded)
        {
            return true;
        }

        lock (FrameworkLoadLock)
        {
            if (_frameworksLoaded)
            {
                return true;
            }

            var foundation = DlOpen(FoundationPath, RtldNow);
            if (foundation == IntPtr.Zero)
            {
                PluginLog.Error($"macro-claude: dlopen({FoundationPath}) returned NULL");
                return false;
            }

            var appKit = DlOpen(AppKitPath, RtldNow);
            if (appKit == IntPtr.Zero)
            {
                PluginLog.Error($"macro-claude: dlopen({AppKitPath}) returned NULL");
                return false;
            }

            PluginLog.Info("macro-claude: Foundation + AppKit frameworks loaded for NativeActivator");
            _frameworksLoaded = true;
            return true;
        }
    }
}
