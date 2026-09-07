using UnityEditor.Build;

namespace JumpNowBro.Editor
{
    internal sealed class LegalBuildProcessor : BuildPlayerProcessor
    {
        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
        {
            buildPlayerContext.AddAdditionalPathToStreamingAssets(
                "LICENSE", "Legal/LICENSE.txt");
            buildPlayerContext.AddAdditionalPathToStreamingAssets(
                "THIRD_PARTY_NOTICES.md", "Legal/THIRD_PARTY_NOTICES.md");
            buildPlayerContext.AddAdditionalPathToStreamingAssets(
                "Assets/Fonts/Inter-OFL.txt", "Legal/Inter-OFL.txt");
            buildPlayerContext.AddAdditionalPathToStreamingAssets(
                "Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt", "Legal/LiberationSans-OFL.txt");
            buildPlayerContext.AddAdditionalPathToStreamingAssets(
                "Assets/Sprites/Kenney/Kenney-Pixel-Platformer-CC0.txt", "Legal/Kenney-Pixel-Platformer-CC0.txt");
        }
    }
}
