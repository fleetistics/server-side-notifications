using Shouldly;

namespace exs.notifications_contracts.Tests
{
	/// <summary>The tags that say what must not be kept after a send. Used by the sender, by the hosts' own copies and by the template renderer, so the rules are pinned here once.</summary>
	public class SecureMarkupTests
	{
		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("no tags at all")]
		[InlineData("a <secure>b</secure> c")]
		[InlineData("<secure>a</secure> and <secure>b</secure>")]
		[InlineData("<secure>outer <secure>inner</secure> still outer</secure>")]
		[InlineData("A <SECURE>shouting</Secure> tag")]
		[InlineData("1 < 2 and 3 > 2, <b>bold</b>")]
		public void PairedTagsAreValid(string? text)
		{
			SecureMarkup.Validate(text).ShouldBeNull();
		}

		[Fact]
		public void ACloseWithoutAnOpenIsRefused()
		{
			SecureMarkup.Validate("code</secure>").ShouldNotBeNull();
		}

		[Fact]
		public void AnOpenWithoutAClosingIsRefused()
		{
			SecureMarkup.Validate("a <secure>never closed").ShouldNotBeNull();
			SecureMarkup.Validate("<secure><secure>x</secure>").ShouldNotBeNull();
		}

		[Fact]
		public void TheProblemTextNeverContainsTheMessage()
		{
			SecureMarkup.Validate("my-secret-value </secure>")!.ShouldNotContain("my-secret-value");
		}

		[Fact]
		public void StripRemovesTheTagsAndKeepsWhatTheyEnclose()
		{
			SecureMarkup.Strip("Your code is <secure>123456</secure>, valid 10 min").ShouldBe("Your code is 123456, valid 10 min");
			SecureMarkup.Strip("<secure>a <secure>b</secure> c</secure>").ShouldBe("a b c");
			SecureMarkup.Strip("<a href=\"<secure>https://x/y?t=1</secure>\">link</a>").ShouldBe("<a href=\"https://x/y?t=1\">link</a>");
			SecureMarkup.Strip("nothing here").ShouldBe("nothing here");
			SecureMarkup.Strip(null).ShouldBeNull();
		}

		[Fact]
		public void MaskReplacesTheTagsAndWhatTheyEncloseWithAFixedMask()
		{
			SecureMarkup.Mask("Your code is <secure>123456</secure>, valid 10 min").ShouldBe("Your code is XXXX, valid 10 min");
			SecureMarkup.Mask("<secure>a</secure> and <secure>much longer</secure>").ShouldBe("XXXX and XXXX");
			SecureMarkup.Mask("<a href=\"<secure>https://x/y?t=1</secure>\">link</a>").ShouldBe("<a href=\"XXXX\">link</a>");
			SecureMarkup.Mask("nothing here").ShouldBe("nothing here");
			SecureMarkup.Mask("").ShouldBe("");
			SecureMarkup.Mask(null).ShouldBeNull();
		}

		[Fact]
		public void TheOutermostPairDecidesWhatIsMasked()
		{
			SecureMarkup.Mask("a <secure>x <secure>y</secure> z</secure> b").ShouldBe("a XXXX b");
		}

		[Fact]
		public void MaskFailsSafeOnTextThatDoesNotPair()
		{
			// Run after the send: it must never leave a secret behind, so an open tag masks to the end ...
			SecureMarkup.Mask("a <secure>secret that is never closed").ShouldBe("a XXXX");
			// ... and a stray close is only dropped.
			SecureMarkup.Mask("a</secure> b").ShouldBe("a b");
		}

		[Fact]
		public void TheTagsAreMatchedWithoutRegardToCase()
		{
			SecureMarkup.Mask("x <SECURE>s</Secure> y").ShouldBe("x XXXX y");
			SecureMarkup.Strip("x <SECURE>s</Secure> y").ShouldBe("x s y");
		}

		[Fact]
		public void HasTagsSaysWhetherThereIsAnyTag()
		{
			SecureMarkup.HasTags("a <secure>b</secure>").ShouldBeTrue();
			SecureMarkup.HasTags("a <b>b</b>").ShouldBeFalse();
			SecureMarkup.HasTags(null).ShouldBeFalse();
		}

		[Theory]
		[InlineData("Bob</secure> <secure>")]
		[InlineData("<secure>")]
		[InlineData("</SECURE>")]
		[InlineData("<</secure>")]
		[InlineData("<secure<secure>>")]
		[InlineData("</secur</secure>e>")]
		public void ANeutralizedValueCanNeverFormATag(string value)
		{
			var neutral = SecureMarkup.Neutralize(value);

			SecureMarkup.HasTags(neutral).ShouldBeFalse(neutral);
			// And so it cannot change what a template's own tags enclose.
			SecureMarkup.Mask($"x <secure>{neutral}</secure> y").ShouldBe("x XXXX y");
		}

		[Theory]
		[InlineData("")]
		[InlineData("plain")]
		[InlineData("1 < 2")]
		[InlineData("<b>bold</b>")]
		[InlineData("<section>")]
		public void OrdinaryValuesAreLeftAlone(string value)
		{
			SecureMarkup.Neutralize(value).ShouldBe(value);
		}
	}
}
