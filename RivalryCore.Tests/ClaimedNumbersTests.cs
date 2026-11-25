namespace Rivalry.Tests;

/// <summary>
/// Every dialogue code Rivalry Core claims is in the README's <c>## Numbers</c> list, which <see cref="NumbersTests"/> keeps inside the block.
/// </summary>
public sealed class ClaimedNumbersTests
{
    [Fact]
    public void Every_claimed_dialogue_code_is_listed_in_the_readme()
    {
        var listed = NumbersTests.Listed();
        int[] claimed = [RivalryMod.GrudgeCondition, RivalryMod.NotorietyCondition, RivalryMod.GrudgeEffect];

        Assert.All(claimed, n => Assert.Contains(n, listed));
    }
}
