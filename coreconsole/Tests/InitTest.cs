using coreconsole;
using coreconsole.utils;

namespace Tests;

/// <summary>
/// The tests of the initialization of the engine: the stack of PKHeX/ALM is loaded once, the
/// sprites are only loaded for the summary (S6) and the SDK of Sentry is only started when there
/// is a DSN to send the events to (S7).
/// </summary>
[TestFixture]
public class InitTest
{
    [Test]
    public void TheStackOfPkhexAndAlmIsLoadedOnlyOnce()
    {
        Helpers.Init();
        Assert.That(Helpers.StackInitialized, Is.True);

        // The second load does not load anything again: the serve loop keeps the stack hot
        // between the requests.
        Helpers.Init();
        Assert.That(Helpers.StackInitialized, Is.True);
    }

    [Test]
    public void OnlyTheSummaryLoadsTheSprites()
    {
        var before = Helpers.SpritesInitialized;

        // The legality does not need the sprites, so it does not load them (S6).
        var answers = ServeTest.Answers(ServeTest.Request("legality", ServeTest.LegalRiolu, "Gen7"));
        Assert.That(answers, Has.Count.EqualTo(1));
        Assert.That(answers[0].ExitCode, Is.EqualTo(0));
        Assert.That(Helpers.SpritesInitialized, Is.EqualTo(before),
            "the legality does not load the sprites");

        // The summary does need them.
        ServeTest.Answers(ServeTest.Request("summary", ServeTest.LegalRiolu, "Gen7"));
        Assert.That(Helpers.SpritesInitialized, Is.True, "the summary loads the sprites");
    }

    [Test]
    public void TheSentrySdkIsNotStartedWithoutADsn()
    {
        var previous = Environment.GetEnvironmentVariable("SENTRY_DSN");
        try
        {
            // The DSN is empty: there is nowhere to send the events to, so the SDK is not
            // started at all (S7).
            Environment.SetEnvironmentVariable("SENTRY_DSN", "");
            Assert.That(MainClass.InitializeSentry(), Is.Null);

            // The DSN is not there either.
            Environment.SetEnvironmentVariable("SENTRY_DSN", null);
            Assert.That(MainClass.InitializeSentry(), Is.Null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SENTRY_DSN", previous);
        }
    }
}
