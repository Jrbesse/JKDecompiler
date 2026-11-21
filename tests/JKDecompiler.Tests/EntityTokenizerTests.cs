using JKDecompiler.Core;
using Xunit;

namespace JKDecompiler.Tests;

public class EntityTokenizerTests
{
    [Fact]
    public void ParsesSimpleEntities()
    {
        var es = BspEntityParser.Parse("{\n\"classname\" \"worldspawn\"\n}\n{\n\"classname\" \"info_player_start\"\n\"origin\" \"1 2 3\"\n}\n");
        Assert.Equal(2, es.Count);
        Assert.Equal("worldspawn", es[0].ClassName);
        Assert.Equal("info_player_start", es[1].ClassName);
        Assert.Equal("1 2 3", es[1].KeyValues["origin"]);
    }

    [Fact]
    public void HandlesValueContainingBrace()
    {
        // the old regex \{[^}]*\} terminated the entity at this '}'
        var es = BspEntityParser.Parse("{\n\"classname\" \"misc_model\"\n\"usescript\" \"void think() { }\n\"\n\"script\" \"if (a) { b(); }\"\n}\n");
        Assert.Single(es);
        Assert.Contains("}", es[0].KeyValues["usescript"]);
        Assert.Equal("if (a) { b(); }", es[0].KeyValues["script"]);
    }

    [Fact]
    public void HandlesEscapedQuotes()
    {
        var es = BspEntityParser.Parse("{\n\"classname\" \"misc_model\"\n\"msg\" \"say \\\"hello\\\" now\"\n}\n");
        Assert.Single(es);
        Assert.Equal("say \"hello\" now", es[0].KeyValues["msg"]);
    }

    [Fact]
    public void KeyWithoutValueThrows()
    {
        Assert.Throws<FormatException>(() => BspEntityParser.Parse("{\n\"classname\" \"worldspawn\"\n\"origin\"\n}"));
    }

    [Fact]
    public void NestedBraceThrows()
    {
        Assert.Throws<FormatException>(() => BspEntityParser.Parse("{\n\"classname\" \"worldspawn\"\n{\n}"));
    }

    [Fact]
    public void DuplicateKeysLastWins()
    {
        var es = BspEntityParser.Parse("{\n\"classname\" \"a\"\n\"classname\" \"b\"\n}");
        Assert.Equal("b", es[0].KeyValues["classname"]);
    }
}
