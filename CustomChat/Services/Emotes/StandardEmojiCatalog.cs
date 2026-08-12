namespace CustomChat.Services.Emotes;

/// <summary>
/// A curated set of common Unicode emoji (name -> hex codepoint, Twemoji's naming convention),
/// used to build <see cref="Models.EmoteDefinition"/> entries whose image comes from a CDN instead
/// of a live BTTV/7TV-style API. Not exhaustive Unicode coverage - just enough well-known emoji to
/// be useful, picked to avoid characters that need a "-fe0f" variation-selector suffix in the CDN
/// filename (a handful of common symbols like ❤/☀/☕ do need it and are omitted here to keep this
/// simple, except heart, which is common enough to be worth the one exception).
/// </summary>
public static class StandardEmojiCatalog
{
    public static readonly (string Code, string Codepoint)[] Entries =
    {
        ("grinning", "1f600"), ("smiley", "1f603"), ("smile", "1f604"), ("grin", "1f601"),
        ("laughing", "1f606"), ("sweat_smile", "1f605"), ("rofl", "1f923"), ("joy", "1f602"),
        ("slight_smile", "1f642"), ("upside_down", "1f643"), ("wink", "1f609"), ("blush", "1f60a"),
        ("innocent", "1f607"), ("heart_eyes", "1f60d"), ("kissing_heart", "1f618"), ("tongue", "1f61b"),
        ("zany", "1f92a"), ("nerd", "1f913"), ("sunglasses", "1f60e"), ("star_struck", "1f929"),
        ("partying", "1f973"), ("relieved", "1f60c"), ("pensive", "1f614"), ("sleepy", "1f62a"),
        ("drooling", "1f924"), ("sleeping", "1f634"), ("mask", "1f637"), ("thermometer_face", "1f912"),
        ("head_bandage", "1f915"), ("nauseated", "1f922"), ("exploding_head", "1f92f"), ("cowboy", "1f920"),
        ("worried", "1f61f"), ("cry", "1f622"), ("sob", "1f62d"), ("scream", "1f631"),
        ("fearful", "1f628"), ("cold_sweat", "1f630"), ("grimacing", "1f62c"), ("angry", "1f620"),
        ("rage", "1f621"), ("triumph", "1f624"), ("thinking", "1f914"), ("shush", "1f92b"),
        ("thumbsup", "1f44d"), ("thumbsdown", "1f44e"), ("clap", "1f44f"), ("wave", "1f44b"),
        ("raised_hands", "1f64c"), ("open_hands", "1f450"), ("pray", "1f64f"), ("muscle", "1f4aa"),
        ("handshake", "1f91d"), ("ok_hand", "1f44c"),
        ("heart", "2764-fe0f"), ("heartbeat", "1f493"), ("two_hearts", "1f495"),
        ("sparkling_heart", "1f496"), ("broken_heart", "1f494"), ("fire", "1f525"), ("star", "2b50"),
        ("sparkles", "2728"), ("tada", "1f389"), ("confetti_ball", "1f38a"), ("hundred", "1f4af"),
        ("check", "2705"), ("cross", "274c"), ("poop", "1f4a9"), ("skull", "1f480"),
        ("ghost", "1f47b"), ("crown", "1f451"), ("gem", "1f48e"), ("moneybag", "1f4b0"),
        ("rocket", "1f680"), ("earth", "1f30d"), ("star2", "1f31f"), ("rainbow", "1f308"),
        ("beer", "1f37a"), ("pizza", "1f355"), ("cake", "1f382"), ("gift", "1f381"),
        ("trophy", "1f3c6"), ("dart", "1f3af"), ("eyes", "1f440"), ("alien", "1f47d"),
        ("robot", "1f916"), ("space_invader", "1f47e"),

        // Meme/reaction favourites.
        ("moai", "1f5ff"), ("clown", "1f921"), ("see_no_evil", "1f648"), ("hear_no_evil", "1f649"),
        ("speak_no_evil", "1f64a"), ("middle_finger", "1f595"), ("crossed_fingers", "1f91e"),
        ("pinching_hand", "1f90f"), ("call_me", "1f919"), ("brain", "1f9e0"), ("ufo", "1f6f8"),
        ("explosion", "1f4a5"), ("anger", "1f4a2"), ("zzz", "1f4a4"), ("salute", "1fae1"),
        ("melting", "1fae0"), ("monocle", "1f9d0"), ("raised_eyebrow", "1f928"),
        ("japanese_ogre", "1f479"), ("japanese_goblin", "1f47a"),

        // Food.
        ("hotdog", "1f32d"), ("hamburger", "1f354"), ("fries", "1f35f"), ("taco", "1f32e"),
        ("popcorn", "1f37f"), ("ice_cream", "1f368"), ("cookie", "1f36a"), ("doughnut", "1f369"),
        ("watermelon", "1f349"), ("clinking_beers", "1f37b"),

        // Animals.
        ("cat", "1f431"), ("dog", "1f436"), ("monkey_face", "1f435"), ("chicken", "1f414"),
        ("penguin", "1f427"), ("unicorn", "1f984"), ("dragon_face", "1f409"), ("trex", "1f996"),
        ("shark", "1f988"), ("octopus", "1f419"), ("crab", "1f980"),
    };
}
