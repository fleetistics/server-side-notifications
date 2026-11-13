using System.Text.Json;
using Shouldly;

namespace exs.notifications_contracts.Tests
{
	public class SecureMarkupJsonTests
	{
		/// <summary>The body the way a host really writes it: the contract's own serializer, which escapes '&lt;'.</summary>
		private static string BodyJson(string subject, string? text, string? html) =>
			ContractJson.Serialize(new EmailBody { To = new EmailAddress { Address = "sam@example.com", Name = "Sam" }, Subject = subject, Text = text, Html = html });

		[Fact]
		public void TheTagsAreEscapedInTheRawJson_SoTheWordIsWhatIsLookedFor()
		{
			BodyJson("S", "code <secure>1</secure>", null).ShouldNotContain("<secure>");
		}

		[Fact]
		public void MaskReplacesMarkedPartsInEveryStringValue()
		{
			var json = BodyJson("Code <secure>777</secure>", "Hi <secure>123456</secure>!", "<a href=\"<secure>https://x/y?t=1</secure>\">go</a>");

			var body = ContractJson.Deserialize<EmailBody>(SecureMarkupJson.Mask(json))!;

			body.Subject.ShouldBe("Code XXXX");
			body.Text.ShouldBe("Hi XXXX!");
			body.Html.ShouldBe("<a href=\"XXXX\">go</a>");
			body.To.Address.ShouldBe("sam@example.com");
			SecureMarkupJson.Mask(json).ShouldNotContain("123456");
		}

		[Fact]
		public void StripKeepsTheContentAndDropsTheTags()
		{
			var json = BodyJson("Code <secure>777</secure>", "Hi <secure>123456</secure>!", "<a href=\"<secure>https://x/y?t=1</secure>\">go</a>");

			var body = ContractJson.Deserialize<EmailBody>(SecureMarkupJson.Strip(json))!;

			body.Subject.ShouldBe("Code 777");
			body.Text.ShouldBe("Hi 123456!");
			body.Html.ShouldBe("<a href=\"https://x/y?t=1\">go</a>");
		}

		[Fact]
		public void ABodyWithNoTagsComesBackUntouched()
		{
			var json = BodyJson("Hello", "plain", "<p>secure by design</p>");

			SecureMarkupJson.Mask(json).ShouldBe(json);
			SecureMarkupJson.Strip(json).ShouldBe(json);
		}

		[Fact]
		public void NonLatinTextIsKeptReadable_ButNothingElseChanges()
		{
			var json = BodyJson("Олена <secure>1</secure>", null, null);

			var masked = SecureMarkupJson.Mask(json);

			masked.ShouldContain("Олена XXXX");
			ContractJson.Deserialize<EmailBody>(masked)!.To.Name.ShouldBe("Sam");
		}

		[Fact]
		public void NumbersBooleansNullsAndNestingSurvive()
		{
			const string json = "{\"a\":1.5,\"b\":true,\"c\":null,\"d\":{\"e\":[\"x <secure>y</secure>\",3]}}";

			var masked = JsonDocument.Parse(SecureMarkupJson.Mask(json)).RootElement;

			masked.GetProperty("a").GetDouble().ShouldBe(1.5);
			masked.GetProperty("b").GetBoolean().ShouldBeTrue();
			masked.GetProperty("c").ValueKind.ShouldBe(JsonValueKind.Null);
			masked.GetProperty("d").GetProperty("e")[0].GetString().ShouldBe("x XXXX");
			masked.GetProperty("d").GetProperty("e")[1].GetInt32().ShouldBe(3);
		}

		[Fact]
		public void ValidateNamesTheValueThatDoesNotPair_NeverItsContent()
		{
			var body = JsonDocument.Parse(BodyJson("fine", "code <secure>my-secret-123", "<p>ok</p>")).RootElement;

			var problems = SecureMarkupJson.Validate(body);

			problems.ShouldHaveSingleItem().ShouldStartWith("text:");
			problems[0].ShouldNotContain("my-secret-123");
		}

		[Fact]
		public void ValidateAcceptsAPairedBody()
		{
			SecureMarkupJson.Validate(JsonDocument.Parse(BodyJson("a <secure>b</secure>", "c", null)).RootElement).ShouldBeEmpty();
		}

		[Fact]
		public void ValidateReportsPathsInsideObjectsAndArrays()
		{
			var body = JsonDocument.Parse("{\"data\":{\"items\":[\"ok\",\"bad</secure>\"]}}").RootElement;

			SecureMarkupJson.Validate(body).ShouldHaveSingleItem().ShouldStartWith("data.items[1]:");
		}
	}
}
