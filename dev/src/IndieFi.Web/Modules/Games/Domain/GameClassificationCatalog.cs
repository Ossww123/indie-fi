namespace IndieFi.Web.Modules.Games.Domain;

public sealed record GameClassificationDefinition(
    long Id,
    string Code,
    string DisplayName,
    int SortOrder,
    bool IsActive = true);

public static class GameClassificationCatalog
{
    public const long UnspecifiedGenreId = 1;
    public const long UnspecifiedPlatformId = 1;
    public const string UnspecifiedCode = "unspecified";

    public static IReadOnlyList<GameClassificationDefinition> Genres { get; } =
        Array.AsReadOnly<GameClassificationDefinition>(
        [
            new(UnspecifiedGenreId, UnspecifiedCode, "미지정", 0, IsActive: false),
            new(10, "action", "액션", 10),
            new(11, "adventure", "어드벤처", 20),
            new(12, "role-playing", "롤플레잉(RPG)", 30),
            new(13, "strategy", "전략", 40),
            new(14, "simulation", "시뮬레이션", 50),
            new(15, "puzzle", "퍼즐", 60),
            new(16, "platformer", "플랫포머", 70),
            new(17, "shooter", "슈팅", 80),
            new(18, "racing", "레이싱", 90),
            new(19, "sports", "스포츠", 100),
            new(20, "fighting", "대전 격투", 110),
            new(21, "rhythm", "리듬", 120),
            new(22, "horror", "공포", 130),
            new(23, "visual-novel", "비주얼 노벨", 140),
            new(24, "roguelike", "로그라이크", 150),
            new(25, "survival", "생존", 160),
            new(26, "sandbox", "샌드박스", 170),
            new(27, "card-board", "카드·보드", 180),
            new(28, "casual", "캐주얼", 190),
            new(29, "other", "기타", 200),
        ]);

    public static IReadOnlyList<GameClassificationDefinition> Tags { get; } =
        Array.AsReadOnly<GameClassificationDefinition>(
        [
            new(10, "single-player", "싱글플레이", 10),
            new(11, "multiplayer", "멀티플레이", 20),
            new(12, "online-multiplayer", "온라인 멀티플레이", 30),
            new(13, "local-multiplayer", "로컬 멀티플레이", 40),
            new(14, "cooperative", "협동", 50),
            new(15, "pvp", "PvP", 60),
            new(16, "story-rich", "스토리 중심", 70),
            new(17, "exploration", "탐험", 80),
            new(18, "turn-based", "턴제", 90),
            new(19, "real-time", "실시간", 100),
            new(20, "procedural-generation", "절차적 생성", 110),
            new(21, "replayable", "반복 플레이", 120),
            new(22, "difficult", "높은 난이도", 130),
            new(23, "relaxing", "편안한 플레이", 140),
            new(24, "short", "짧은 플레이", 150),
            new(25, "open-world", "오픈 월드", 160),
            new(26, "2d", "2D", 170),
            new(27, "3d", "3D", 180),
            new(28, "pixel-art", "픽셀 아트", 190),
            new(29, "hand-drawn", "손그림", 200),
            new(30, "retro", "레트로", 210),
            new(31, "atmospheric", "분위기 중심", 220),
            new(32, "controller-support", "컨트롤러 지원", 230),
            new(33, "keyboard-mouse", "키보드·마우스", 240),
            new(34, "touch-controls", "터치 조작", 250),
            new(35, "family-friendly", "가족 친화", 260),
            new(36, "accessibility-options", "접근성 옵션", 270),
            new(37, "experimental", "실험적", 280),
        ]);

    public static IReadOnlyList<GameClassificationDefinition> Platforms { get; } =
        Array.AsReadOnly<GameClassificationDefinition>(
        [
            new(UnspecifiedPlatformId, UnspecifiedCode, "미지정", 0, IsActive: false),
            new(10, "windows", "Windows", 10),
            new(11, "macos", "macOS", 20),
            new(12, "linux", "Linux", 30),
            new(13, "web", "Web", 40),
            new(14, "android", "Android", 50),
            new(15, "ios", "iOS", 60),
            new(16, "steam-deck", "Steam Deck", 70),
            new(17, "playstation-5", "PlayStation 5", 80),
            new(18, "xbox-series", "Xbox Series X|S", 90),
            new(19, "nintendo-switch", "Nintendo Switch", 100),
            new(20, "virtual-reality", "VR", 110),
            new(21, "other", "기타 플랫폼", 120),
        ]);
}
