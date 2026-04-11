using System;

namespace Loupedeck.MacroClaudePlugin.Actions;

public sealed class ClaudeSlot7Command : SlotCommandBase
{
    public ClaudeSlot7Command()
        : base(displayNumber: 7)
    {
    }

    protected override Int32 SlotIndex => 6;
}
