using System.Reflection;
using System.Runtime.CompilerServices;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Infrastructure.Email;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// Every public exception type of MailKit and MimeKit.Core, held to a row that says what the SMTP adapter does
/// with it: rethrown in the adapter's own words by one of its catches, unable to reach the adapter's path, or let
/// through because neither its message nor a member can hold an address or a relay reply. Whatever leaves the
/// adapter, the outbox writes into its row, its span and its retry log line (<c>docs/threat-model.md</c>), so a
/// MailKit release that adds a type fails here until someone reads what the new type can carry and gives it a row.
/// A rethrown row names the catch that takes the type, read from the adapter's compiled catch clauses.
/// <para>
/// What this does not catch: a listed type whose message starts quoting an address or a reply in a later release
/// (only MailKit's release notes say so, <c>.claude/rules/infrastructure.md</c>), and the BCL exceptions MailKit
/// throws (<see cref="IOException"/>, a socket error, <see cref="NotSupportedException"/>), which are not its types.
/// </para>
/// </summary>
public sealed class MailKitExceptionTypeTests
{
    private static readonly Assembly[] MailKitAndMimeKit = [typeof(SmtpClient).Assembly, typeof(MimeMessage).Assembly];

    /// <summary>The rows, by full type name. A new type's row comes from reading what its message and members hold.</summary>
    private static readonly Dictionary<string, Treatment> Treatments = new(StringComparer.Ordinal)
    {
        ["MailKit.Net.Smtp.SmtpCommandException"] = Rethrown(typeof(SmtpCommandException), "the relay's reply, and Mailbox the full address"),
        ["MailKit.Net.Smtp.SmtpProtocolException"] = Rethrown(typeof(SmtpProtocolException), "the relay's last reply"),
        ["MailKit.Security.AuthenticationException"] = Rethrown(typeof(AuthenticationException), "the relay's reply to AUTH"),
        ["MailKit.Security.SaslException"] = Rethrown(typeof(AuthenticationException), "a SASL mechanism's failure, through its base"),
        ["MailKit.ServiceNotAuthenticatedException"] = Rethrown(typeof(ServiceNotAuthenticatedException), "the relay's 530 reply"),
        ["MailKit.Net.Smtp.SmtpServiceNotAuthenticatedException"] = Rethrown(typeof(ServiceNotAuthenticatedException), "MailKit 5's 530 with its ResponseText, through its base"),
        ["MailKit.Net.Smtp.SmtpUtf8AddressException"] = Rethrown(typeof(SmtpUtf8AddressException), "MailKit's SMTPUTF8 address check quotes the address, and Mailbox is it"),

        ["MailKit.CommandException"] = CannotReach("abstract: thrown only as a subtype, each with its own row"),
        ["MailKit.ProtocolException"] = CannotReach("abstract: thrown only as a subtype, each with its own row"),
        ["MailKit.FolderNotFoundException"] = CannotReach("a mail store's folders: the adapter only submits"),
        ["MailKit.FolderNotOpenException"] = CannotReach("a mail store's folders: the adapter only submits"),
        ["MailKit.MessageNotFoundException"] = CannotReach("fetching from a mail store: the adapter only submits"),
        ["MailKit.Net.Imap.ImapCommandException"] = CannotReach("IMAP"),
        ["MailKit.Net.Imap.ImapProtocolException"] = CannotReach("IMAP"),
        ["MailKit.Net.Pop3.Pop3CommandException"] = CannotReach("POP3"),
        ["MailKit.Net.Pop3.Pop3ProtocolException"] = CannotReach("POP3"),
        ["MailKit.Net.Proxy.ProxyProtocolException"] = CannotReach("a proxy: the adapter sets no ProxyClient"),
        ["MimeKit.Tnef.TnefException"] = CannotReach("the TNEF reader: the adapter reads none"),

        ["MailKit.Security.SslHandshakeException"] = PassesThrough("STARTTLS failed: the relay's host, its certificate's DNS names and the chain's status; the certificates it keeps are the relay's"),
        ["MailKit.ServiceNotConnectedException"] = PassesThrough("MailKit's constant texts (\"The SmtpClient is not connected.\", \"... must be connected before you can authenticate.\"): no address, no reply"),
        ["MimeKit.ParseException"] = PassesThrough("a From or To that does not parse: an offset and at most one character of the text"),
    };

    private enum Handling
    {
        Rethrown,
        CannotReach,
        PassesThrough,
    }

    [Fact]
    public void Every_public_exception_type_of_MailKit_and_MimeKit_Core_has_a_row_that_matches_the_adapter()
    {
        MailKitAndMimeKit.Select(static assembly => assembly.GetName().Name).ShouldBe(["MailKit", "MimeKit.Core"]);
        Treatments.ShouldAllBe(static row => row.Value.Why.Length > 0);

        Problems(ExceptionTypes(), Treatments, AdapterCatches()).ShouldBeEmpty();
    }

    [Fact]
    public void The_exception_type_check_flags_a_type_without_a_row_a_row_without_a_type_and_a_row_the_catches_do_not_bear_out()
    {
        // SmtpFailedRecipientException is the BCL's, and its message quotes the address: the shape a new MailKit type could have.
        Dictionary<string, Treatment> planted = new(Treatments, StringComparer.Ordinal)
        {
            ["MailKit.Net.Smtp.SmtpRemovedException"] = CannotReach("planted: a type no release has"),
            ["MailKit.ServiceNotConnectedException"] = Rethrown(typeof(ServiceNotAuthenticatedException), "planted: a catch the adapter does not have"),
            ["MailKit.Security.SaslException"] = PassesThrough("planted: a catch takes it"),
        };

        IReadOnlyList<string> problems = Problems(
            [.. ExceptionTypes(), typeof(System.Net.Mail.SmtpFailedRecipientException)], planted, AdapterCatches());

        problems.ShouldBe(
        [
            "System.Net.Mail.SmtpFailedRecipientException has no row: read what its message and members can hold, then give it one",
            "MailKit.Net.Smtp.SmtpRemovedException has a row but no type in MailKit or MimeKit.Core: remove the row",
            "MailKit.ServiceNotConnectedException is rethrown by the catch of ServiceNotAuthenticatedException, says its row, but the adapter's catch that takes it is none",
            "MailKit.Security.SaslException is PassesThrough, says its row, but the adapter's catch of AuthenticationException takes it",
        ], ignoreOrder: true);
    }

    private static List<Type> ExceptionTypes()
        => [.. MailKitAndMimeKit.SelectMany(static assembly => assembly.GetExportedTypes()).Where(static type => typeof(Exception).IsAssignableFrom(type))];

    /// <summary>
    /// The exception types <c>SmtpEmailSender.SendAsync</c> catches, in its order, from the compiled state machine;
    /// the machine's own catch of <see cref="Exception"/>, which hands any failure to the returned task, is left out.
    /// </summary>
    private static List<Type> AdapterCatches()
    {
        Type adapter = typeof(EmailOptions).Assembly.GetType("Trellis.Infrastructure.Email.SmtpEmailSender", throwOnError: true)!;
        Type machine = adapter.GetMethod(nameof(IEmailSender.SendAsync))!.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        IList<ExceptionHandlingClause> clauses = machine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethodBody()!.ExceptionHandlingClauses;
        List<Type> catches = [.. clauses
            .Where(static clause => clause.Flags == ExceptionHandlingClauseOptions.Clause && clause.CatchType != typeof(Exception))
            .Select(static clause => clause.CatchType!)];
        catches.ShouldNotBeEmpty();
        return catches;
    }

    private static List<string> Problems(IReadOnlyCollection<Type> types, IReadOnlyDictionary<string, Treatment> rows, IReadOnlyList<Type> catches)
    {
        var problems = new List<string>();
        foreach (Type type in types.Where(type => !rows.ContainsKey(type.FullName!)))
        {
            problems.Add($"{type.FullName} has no row: read what its message and members can hold, then give it one");
        }

        foreach (string name in rows.Keys.Where(name => !types.Any(type => type.FullName == name)))
        {
            problems.Add($"{name} has a row but no type in MailKit or MimeKit.Core: remove the row");
        }

        foreach (Type type in types.Where(type => rows.ContainsKey(type.FullName!)))
        {
            Treatment row = rows[type.FullName!];
            Type? takenBy = catches.FirstOrDefault(caught => caught.IsAssignableFrom(type));
            if (row.Handling == Handling.Rethrown && takenBy != row.CaughtBy)
            {
                problems.Add($"{type.FullName} is rethrown by the catch of {row.CaughtBy!.Name}, says its row, but the adapter's catch that takes it is {takenBy?.Name ?? "none"}");
            }
            else if (row.Handling != Handling.Rethrown && takenBy is not null)
            {
                problems.Add($"{type.FullName} is {row.Handling}, says its row, but the adapter's catch of {takenBy.Name} takes it");
            }
        }

        return problems;
    }

    private static Treatment Rethrown(Type caughtBy, string carries) => new(Handling.Rethrown, caughtBy, carries);

    private static Treatment CannotReach(string why) => new(Handling.CannotReach, null, why);

    private static Treatment PassesThrough(string why) => new(Handling.PassesThrough, null, why);

    /// <summary>How the adapter treats a type; <see cref="Why"/> is what the type carries, or why it cannot reach the adapter.</summary>
    private sealed record Treatment(Handling Handling, Type? CaughtBy, string Why);
}
