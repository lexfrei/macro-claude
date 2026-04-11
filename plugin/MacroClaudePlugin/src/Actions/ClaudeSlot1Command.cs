using System;

namespace Loupedeck.MacroClaudePlugin.Actions;

public sealed class ClaudeSlot1Command : SlotCommandBase
{
    public ClaudeSlot1Command()
        : base(displayNumber: 1)
    {
    }

    protected override Int32 SlotIndex => 0;
}
