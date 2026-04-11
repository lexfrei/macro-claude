#nullable enable
namespace Loupedeck.MacroClaudePlugin.Focus
{
    using System;
    using System.Runtime.InteropServices;

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
    internal static class NativeActivator
    {
        private const String LibObjc = "/usr/lib/libobjc.A.dylib";

        // NSApplicationActivateIgnoringOtherApps = 1 << 1 = 2
        private const UInt64 NSApplicationActivateIgnoringOtherApps = 2;

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
                return false;
            }

            try
            {
                // [[NSString alloc] initWithUTF8String:bundleId]
                var nsStringCls = GetClass("NSString");
                if (nsStringCls == IntPtr.Zero)
                {
                    return false;
                }
                var selAlloc = RegisterSel("alloc");
                var selInitUtf8 = RegisterSel("initWithUTF8String:");
                var allocated = MsgSend(nsStringCls, selAlloc);
                if (allocated == IntPtr.Zero)
                {
                    return false;
                }
                var nsBundleId = MsgSendStr(allocated, selInitUtf8, bundleId);
                if (nsBundleId == IntPtr.Zero)
                {
                    return false;
                }

                // [NSRunningApplication runningApplicationsWithBundleIdentifier:nsBundleId]
                var runningAppCls = GetClass("NSRunningApplication");
                if (runningAppCls == IntPtr.Zero)
                {
                    return false;
                }
                var selRunningApps = RegisterSel("runningApplicationsWithBundleIdentifier:");
                var apps = MsgSend(runningAppCls, selRunningApps, nsBundleId);
                if (apps == IntPtr.Zero)
                {
                    return false;
                }

                // [apps count]
                var selCount = RegisterSel("count");
                var countPtr = MsgSend(apps, selCount);
                if (countPtr == IntPtr.Zero)
                {
                    return false;
                }

                // [apps firstObject]
                var selFirst = RegisterSel("firstObject");
                var firstApp = MsgSend(apps, selFirst);
                if (firstApp == IntPtr.Zero)
                {
                    return false;
                }

                // [firstApp activateWithOptions:2]
                var selActivate = RegisterSel("activateWithOptions:");
                var result = MsgSendUInt64(firstApp, selActivate, NSApplicationActivateIgnoringOtherApps);
                return result != IntPtr.Zero;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }
    }
}
