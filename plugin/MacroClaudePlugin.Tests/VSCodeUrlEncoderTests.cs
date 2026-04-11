using System;

using Loupedeck.MacroClaudePlugin.Focus;

using Xunit;

namespace Loupedeck.MacroClaudePlugin.Tests;

public sealed class VSCodeUrlEncoderTests
{
    [Fact]
    public void Simple_Absolute_Path_Roundtrips_Unchanged()
    {
        Assert.Equal(
            "/Users/lex/code/macro-claude",
            VSCodeUrlEncoder.EncodePath("/Users/lex/code/macro-claude"));
    }

    [Fact]
    public void Path_With_Hash_Is_Percent_Encoded()
    {
        // The regression guard: a path with '#' would otherwise
        // become a URL fragment, and LaunchServices would open
        // /Users/lex/code/bug instead of /Users/lex/code/bug#42.
        Assert.Equal(
            "/Users/lex/code/bug%2342",
            VSCodeUrlEncoder.EncodePath("/Users/lex/code/bug#42"));
    }

    [Fact]
    public void Path_With_Question_Mark_Is_Percent_Encoded()
    {
        Assert.Equal(
            "/Users/lex/code/what%3Fnow",
            VSCodeUrlEncoder.EncodePath("/Users/lex/code/what?now"));
    }

    [Fact]
    public void Path_With_Percent_Is_Percent_Encoded()
    {
        Assert.Equal(
            "/Users/lex/code/100%25",
            VSCodeUrlEncoder.EncodePath("/Users/lex/code/100%"));
    }

    [Fact]
    public void Path_With_Space_Is_Percent_Encoded()
    {
        // VS Code parses "%20" and unencoded spaces differently in
        // some versions — stick with the encoded form.
        Assert.Equal(
            "/Users/lex/code/my%20project",
            VSCodeUrlEncoder.EncodePath("/Users/lex/code/my project"));
    }

    [Fact]
    public void Path_With_Cyrillic_Is_Utf8_Percent_Encoded()
    {
        // Uri.EscapeDataString uses UTF-8 bytes — "п" is C0 BF.
        Assert.Equal(
            "/Users/lex/code/%D0%BF%D1%80%D0%BE%D0%B5%D0%BA%D1%82",
            VSCodeUrlEncoder.EncodePath("/Users/lex/code/проект"));
    }

    [Fact]
    public void Trailing_Slash_Is_Collapsed()
    {
        Assert.Equal(
            "/Users/lex/code",
            VSCodeUrlEncoder.EncodePath("/Users/lex/code/"));
    }

    [Fact]
    public void Double_Slash_Is_Collapsed()
    {
        Assert.Equal(
            "/Users/lex/code",
            VSCodeUrlEncoder.EncodePath("/Users//lex//code"));
    }

    [Fact]
    public void Root_Path_Returns_Single_Slash()
    {
        Assert.Equal("/", VSCodeUrlEncoder.EncodePath("/"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_Input_Returns_Single_Slash(String? input)
    {
        Assert.Equal("/", VSCodeUrlEncoder.EncodePath(input!));
    }

    [Fact]
    public void Unreserved_Characters_Pass_Through_Unchanged()
    {
        // RFC 3986 unreserved: letters, digits, - _ . ~
        Assert.Equal(
            "/a-b_c.d~e",
            VSCodeUrlEncoder.EncodePath("/a-b_c.d~e"));
    }

    [Fact]
    public void Ampersand_And_At_Sign_Are_Encoded()
    {
        Assert.Equal(
            "/Users/lex/code/me%40host%26params",
            VSCodeUrlEncoder.EncodePath("/Users/lex/code/me@host&params"));
    }
}
