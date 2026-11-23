using JKDecompiler.Core;
using Xunit;

namespace JKDecompiler.Tests;

public class RedProofTest
{
    [Fact]
    public void Intentional_Gate_Break() => Assert.True(false, "red-PR proof: the gate must fail on this commit");
}
