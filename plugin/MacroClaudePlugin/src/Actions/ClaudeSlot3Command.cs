using System;

namespace Loupedeck.MacroClaudePlugin.Actions;

public sealed class ClaudeSlot3Command : SlotCommandBase
{
    public ClaudeSlot3Command()
        : base(displayNumber: 3)
    {
    }

    protected override Int32 SlotIndex => 2;
}
