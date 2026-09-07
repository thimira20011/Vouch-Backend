using Vouch.Domain.Enums;

namespace Vouch.Application.Features.AiWingman;

public record FallbackIcebreaker(
    IntellectualInterest Interest,
    string Text,
    string Tag
);

public static class CuratedIcebreakers
{
    public static readonly IReadOnlyList<FallbackIcebreaker> Library = new List<FallbackIcebreaker>
    {
        // Philosophy
        new(IntellectualInterest.Philosophy, "Do you believe character is fundamentally revealed in how someone treats strangers, or how they handle disappointment?", "ethics"),
        new(IntellectualInterest.Philosophy, "Which philosophical paradox do you find yourself thinking about when you can't fall asleep?", "existential"),
        new(IntellectualInterest.Philosophy, "Stoicism argues that we control only our reactions. How has that idea played out in your university journey?", "stoicism"),
        new(IntellectualInterest.Philosophy, "If you had to choose one virtue to cultivate above all others this year, what would it be?", "virtue-ethics"),
        new(IntellectualInterest.Philosophy, "Do you think true altruism exists, or is kindness always mutually beneficial in some subtle way?", "human-nature"),
        new(IntellectualInterest.Philosophy, "Is knowledge acquired primarily through direct experience or through structured contemplation?", "epistemology"),
        new(IntellectualInterest.Philosophy, "What is a belief you held deeply two years ago that you have quietly abandoned?", "beliefs"),
        new(IntellectualInterest.Philosophy, "Do you lean more towards finding meaning or creating meaning from scratch?", "existentialism"),

        // Literature
        new(IntellectualInterest.Literature, "What single passage in a book stopped you in your tracks and forced you to look away from the page for a moment?", "prose"),
        new(IntellectualInterest.Literature, "Which fictional character's internal dilemmas felt uncomfortably close to your own thoughts?", "characters"),
        new(IntellectualInterest.Literature, "Do you prefer literature that acts as a mirror to your world or a window into an unfamiliar mind?", "perspectives"),
        new(IntellectualInterest.Literature, "Is there a poem or short essay you return to whenever you need clarity during heavy academic terms?", "poetry"),
        new(IntellectualInterest.Literature, "What book would you hand to someone if you wanted them to understand who you really are without speaking?", "recommendations"),
        new(IntellectualInterest.Literature, "How do you think the rhythm of reading slow literature shapes our patience in relationships?", "slow-reading"),
        new(IntellectualInterest.Literature, "Do you believe tragic endings in stories leave a more enduring impression than triumphant ones?", "storytelling"),
        new(IntellectualInterest.Literature, "Which author's prose feels to you like listening to classical chamber music?", "aesthetic"),

        // Architecture
        new(IntellectualInterest.Architecture, "Which space on our campus has the most soothing ambient light in the late afternoon?", "campus-spaces"),
        new(IntellectualInterest.Architecture, "Do you find yourself drawn more to timeless brutalist stone or minimalist wood and natural textures?", "materials"),
        new(IntellectualInterest.Architecture, "How do you feel physical spaces influence the depth and honesty of our conversations?", "spatial-psychology"),
        new(IntellectualInterest.Architecture, "What building or courtyard makes you feel quiet and grounded the moment you walk into it?", "sanctuary"),
        new(IntellectualInterest.Architecture, "In a world of glass towers, what traditional architectural element do you wish modern buildings preserved?", "heritage"),
        new(IntellectualInterest.Architecture, "Do you believe buildings should blend seamlessly into natural landscapes or stand in deliberate dialogue with them?", "design-philosophy"),
        new(IntellectualInterest.Architecture, "What is your idea of a sanctuary room for reading and quiet reflection?", "interiority"),
        new(IntellectualInterest.Architecture, "If you could design a study cloister for students seeking focus, what would be its central feature?", "monastic-design"),

        // Music
        new(IntellectualInterest.Music, "What album or composition feels like an intimate conversation between the artist and your subconscious?", "intimacy"),
        new(IntellectualInterest.Music, "Do you listen to music to amplify what you're already feeling, or to gently shift your state of mind?", "reflection"),
        new(IntellectualInterest.Music, "Is there an instrument whose acoustic timbre evokes an immediate sense of nostalgia for you?", "timbre"),
        new(IntellectualInterest.Music, "What song captures the quiet quietude of walking home alone across campus after rain?", "atmosphere"),
        new(IntellectualInterest.Music, "When listening intently, do you find yourself focusing on the lyrics or the harmonic layers underneath?", "structure"),
        new(IntellectualInterest.Music, "What musical piece would you play to introduce someone to the music that formed your taste?", "musical-roots"),
        new(IntellectualInterest.Music, "Do you appreciate deliberate silence between musical notes as much as the notes themselves?", "minimalism"),
        new(IntellectualInterest.Music, "What soundtrack has accompanied your late-night study sessions this semester?", "focus"),

        // Academic Goals
        new(IntellectualInterest.AcademicGoals, "What unresolved question in your field of study genuinely excites you when you think about the next five years?", "research-passion"),
        new(IntellectualInterest.AcademicGoals, "How do you maintain curiosity when academic pressure begins turning learning into an obligation?", "perseverance"),
        new(IntellectualInterest.AcademicGoals, "If you had unlimited resources to investigate one problem without publishing pressure, what would you tackle?", "intellectual-freedom"),
        new(IntellectualInterest.AcademicGoals, "Who is a mentor or thinker whose intellectual integrity you strive to emulate?", "integrity"),
        new(IntellectualInterest.AcademicGoals, "Do you think true mastery requires narrow specialization or polymathic exploration across disciplines?", "mastery"),
        new(IntellectualInterest.AcademicGoals, "What was the most humbling academic concept you had to wrestle with before it clicked?", "learning-curve"),
        new(IntellectualInterest.AcademicGoals, "How do you hope your work will positively impact the local community once you graduate?", "social-impact"),
        new(IntellectualInterest.AcademicGoals, "What habit have you developed at university that has most protected your peace of mind?", "discipline"),

        // Science
        new(IntellectualInterest.Science, "Which discovery in natural science feels most poetic or humbling to contemplate?", "wonder"),
        new(IntellectualInterest.Science, "Do you think humanity's greatest scientific frontier is deep space or the mysteries of human consciousness?", "frontiers"),
        new(IntellectualInterest.Science, "How has studying scientific rigor altered how you evaluate everyday claims and opinions?", "critical-thinking"),
        new(IntellectualInterest.Science, "What natural phenomenon never fails to make you pause in quiet astonishment?", "nature"),
        new(IntellectualInterest.Science, "How do you balance scientific objectivity with emotional intuition in your personal decisions?", "rationality"),
        new(IntellectualInterest.Science, "If you could witness one historical scientific breakthrough in person, which one would it be?", "history-of-science"),
        new(IntellectualInterest.Science, "What scientific concept from outside your major do you wish was common knowledge for everyone?", "interdisciplinary"),
        new(IntellectualInterest.Science, "Do you believe simplicity and elegance in a mathematical formula are signs of physical truth?", "elegance"),

        // General Thoughtful / Cross-Disciplinary (Rounding up to 50 curated prompts)
        new(IntellectualInterest.Philosophy, "What is a small, quiet ritual in your daily routine that keeps you grounded?", "daily-rituals"),
        new(IntellectualInterest.Literature, "If a personal memoir of your university years were written today, what would chapter one be titled?", "reflection")
    };

    public static IReadOnlyList<FallbackIcebreaker> GetIcebreakersForInterests(
        IEnumerable<IntellectualInterest> sharedInterests,
        int count = 3)
    {
        var interestList = sharedInterests.ToList();
        var matching = Library.Where(i => interestList.Contains(i.Interest)).ToList();

        if (matching.Count < count)
        {
            matching.AddRange(Library.Except(matching));
        }

        return matching.Take(count).ToList();
    }
}
