using System;

namespace Loupedeck.MacroClaudePlugin;

// Required by the Loupedeck Plugin Service reflection discovery: every
// plugin assembly must contain at least one concrete ClientApplication
// subclass, otherwise LPS refuses to load the DLL with "Cannot load
// plugin" (even when HasNoApplication=true on the Plugin class).
//
// Keeping this class as an inert stub satisfies LPS without binding
// macro-claude to any specific host application. The plugin is still
// declared as a universal plugin via Plugin.UsesApplicationApiOnly and
// Plugin.HasNoApplication.
public class MacroClaudeApplication : ClientApplication
{
    protected override String GetProcessName() => String.Empty;

    protected override String GetBundleName() => String.Empty;

    public override ClientApplicationStatus GetApplicationStatus()
        => ClientApplicationStatus.Unknown;
}
