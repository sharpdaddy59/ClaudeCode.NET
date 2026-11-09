using Microsoft.Extensions.AI;

namespace ClaudeCode.Extensions.AI.Tests;

public class PromptBuilderTests
{
    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage System(string text) => new(ChatRole.System, text);

    [Fact]
    public void Single_user_message_is_the_prompt_verbatim()
    {
        var built = PromptBuilder.Build([User("Fix the bug\nin Foo.cs")]);

        built.Prompt.Should().Be("Fix the bug\nin Foo.cs");
        built.SystemPrompt.Should().BeNull();
    }

    [Fact]
    public void System_plus_user_splits_into_system_prompt_and_prompt()
    {
        var built = PromptBuilder.Build([System("You are terse."), User("Hi")]);

        built.Prompt.Should().Be("Hi");
        built.SystemPrompt.Should().Be("You are terse.");
    }

    [Fact]
    public void Options_system_prompt_alone_is_used()
    {
        var built = PromptBuilder.Build([User("Hi")], "From options");

        built.SystemPrompt.Should().Be("From options");
    }

    [Fact]
    public void Multiple_system_messages_merge_after_options_system_prompt()
    {
        var built = PromptBuilder.Build(
            [System("First"), User("Hi"), System("Second")],
            "From options");

        built.SystemPrompt.Should().Be("From options\n\nFirst\n\nSecond");
        built.Prompt.Should().Be("Hi", "system messages anywhere in the list never become transcript turns");
    }

    [Fact]
    public void Blank_system_parts_are_ignored()
    {
        var built = PromptBuilder.Build([System("   "), System(""), User("Hi")], "  ");

        built.SystemPrompt.Should().BeNull();
    }

    [Fact]
    public void Multi_turn_history_renders_transcript_with_preamble()
    {
        var built = PromptBuilder.Build(
        [
            System("sys"),
            User("What is 2+2?"),
            Assistant("4"),
            User("And times 3?"),
        ]);

        built.SystemPrompt.Should().Be("sys");
        built.Prompt.Should().Be(
            PromptBuilder.TranscriptPreamble + "\n\n" +
            "User: What is 2+2?\n\n" +
            "Assistant: 4\n\n" +
            "User: And times 3?");
    }

    [Fact]
    public void Two_consecutive_user_messages_render_as_transcript()
    {
        var built = PromptBuilder.Build([User("one"), User("two")]);

        built.Prompt.Should().StartWith(PromptBuilder.TranscriptPreamble)
            .And.EndWith("User: one\n\nUser: two");
    }

    [Fact]
    public void Other_roles_use_capitalized_role_name()
    {
        var built = PromptBuilder.Build([new ChatMessage(ChatRole.Tool, "tool output"), User("next")]);

        built.Prompt.Should().Contain("Tool: tool output\n\nUser: next");
    }

    [Fact]
    public void Multiple_text_contents_in_one_message_are_concatenated()
    {
        var message = new ChatMessage(ChatRole.User, [new TextContent("Hello, "), new TextContent("world")]);

        PromptBuilder.Build([message]).Prompt.Should().Be("Hello, world");
    }

    [Fact]
    public void Prompt_preserves_special_characters()
    {
        const string text = "quotes \" and 'single', $vars, `ticks`, & unicode ✓ — \r\n done";

        PromptBuilder.Build([User(text)]).Prompt.Should().Be(text);
    }

    [Fact]
    public void Accepts_lazy_enumerable_and_enumerates_once()
    {
        var count = 0;
        IEnumerable<ChatMessage> Messages()
        {
            count++;
            yield return System("s");
            yield return User("u");
        }

        PromptBuilder.Build(Messages()).Prompt.Should().Be("u");
        count.Should().Be(1);
    }

    public static TheoryData<AIContent> UnsupportedContents() => new()
    {
        new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
        new FunctionCallContent("call-1", "get_weather"),
        new FunctionResultContent("call-1", "sunny"),
        new UriContent(new Uri("https://example.com/a.png"), "image/png"),
        new TextReasoningContent("thinking"),
    };

    [Theory]
    [MemberData(nameof(UnsupportedContents))]
    public void Unsupported_content_throws_naming_the_type(AIContent content)
    {
        var message = new ChatMessage(ChatRole.User, [new TextContent("look"), content]);

        var act = () => PromptBuilder.Build([message]);

        act.Should().Throw<NotSupportedException>().WithMessage($"*{content.GetType().Name}*");
    }

    [Fact]
    public void Unsupported_content_in_system_or_history_also_throws()
    {
        var history = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c", "f")]);

        var act = () => PromptBuilder.Build([User("a"), history, User("b")]);

        act.Should().Throw<NotSupportedException>().WithMessage("*FunctionCallContent*");
    }

    [Fact]
    public void Empty_list_throws()
    {
        var act = () => PromptBuilder.Build([]);

        act.Should().Throw<ArgumentException>().WithParameterName("messages");
    }

    [Fact]
    public void Null_list_throws()
    {
        var act = () => PromptBuilder.Build(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void All_system_list_throws()
    {
        var act = () => PromptBuilder.Build([System("a"), System("b")], "options");

        act.Should().Throw<ArgumentException>().WithMessage("*non-system*");
    }

    [Fact]
    public void Null_entry_throws()
    {
        var act = () => PromptBuilder.Build([User("a"), null!]);

        act.Should().Throw<ArgumentException>().WithMessage("*null*");
    }

    [Fact]
    public void History_ending_with_assistant_throws()
    {
        var act = () => PromptBuilder.Build([User("a"), Assistant("b")]);

        act.Should().Throw<ArgumentException>().WithMessage("*user role*assistant*");
    }

    [Fact]
    public void Single_assistant_message_throws()
    {
        var act = () => PromptBuilder.Build([Assistant("b")]);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_final_user_message_throws(string text)
    {
        var act = () => PromptBuilder.Build([User(text)]);

        act.Should().Throw<ArgumentException>().WithMessage("*must contain text*");
    }

    [Fact]
    public void User_message_with_no_contents_throws_as_blank()
    {
        var act = () => PromptBuilder.Build([new ChatMessage(ChatRole.User, (string?)null)]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Blank_history_turns_are_kept_in_transcript()
    {
        var built = PromptBuilder.Build([User("a"), Assistant(""), User("b")]);

        built.Prompt.Should().EndWith("User: a\n\nAssistant: \n\nUser: b");
    }

    [Fact]
    public void Resuming_sends_only_messages_after_last_assistant()
    {
        var built = PromptBuilder.Build(
            [System("sys"), User("q1"), Assistant("a1"), User("q2")], "opts", resuming: true);

        built.Prompt.Should().Be("q2");
        built.SystemPrompt.Should().Be("opts\n\nsys");
    }

    [Fact]
    public void Resuming_joins_multiple_new_user_messages()
    {
        var built = PromptBuilder.Build([User("q1"), Assistant("a1"), User("q2"), User(" "), User("q3")], resuming: true);

        built.Prompt.Should().Be("q2\n\nq3");
    }

    [Fact]
    public void Resuming_without_assistant_history_sends_all_user_messages_plainly()
    {
        var built = PromptBuilder.Build([User("a"), User("b")], resuming: true);

        built.Prompt.Should().Be("a\n\nb");
    }

    [Fact]
    public void Resuming_single_message_is_verbatim()
    {
        PromptBuilder.Build([User("only")], resuming: true).Prompt.Should().Be("only");
    }

    [Fact]
    public void Resuming_still_validates_the_messages_it_sends_and_the_history_shape()
    {
        var unsupportedInNewMessage = () => PromptBuilder.Build(
            [User("q"), Assistant("a"), new ChatMessage(ChatRole.User, [new FunctionCallContent("c", "f")])], resuming: true);
        var endsWithAssistant = () => PromptBuilder.Build([User("q"), Assistant("a")], resuming: true);

        unsupportedInNewMessage.Should().Throw<NotSupportedException>();
        endsWithAssistant.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Resuming_skips_unsupported_content_in_turns_the_session_already_holds()
    {
        // Reasoning or tool-call content another provider or middleware left in the history
        var history = new List<ChatMessage>
        {
            User("first question"),
            new(ChatRole.Assistant, [new TextReasoningContent("thinking"), new TextContent("answer"), new FunctionCallContent("c", "f")]),
            User("follow-up"),
        };

        PromptBuilder.Build(history, resuming: true).Prompt.Should().Be("follow-up");

        // Without a session to resume, the whole history is replayed, so it is still rejected
        var replay = () => PromptBuilder.Build(history, resuming: false);
        replay.Should().Throw<NotSupportedException>();
    }
}
