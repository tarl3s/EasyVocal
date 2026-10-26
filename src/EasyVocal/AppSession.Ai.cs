using System.IO;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;

namespace EasyVocal;

/// <summary>Which AI voices a session gets: the data folder's (normal runs), a stand-in (smoke test), the tiny test pack (e2e).</summary>
internal sealed partial class AppSession
{
    private StandInAi? smokeAi;
    private E2E.TinyAiPack? e2ePack;

    private IVoiceConverterSource? MakeAiVoices(AppSettings s)
    {
        if (Args.SmokeTest) return smokeAi = new StandInAi();
        if (Args.E2E)
        {
            // Only with the tiny test models (the headless tests pass them; a packaged run has none: the AI steps are skipped).
            if (Args.E2ERelaunch) return null;
            e2ePack = E2E.TinyAiPack.TryCreate(options.AiFixtures ?? Args.AiFixtures, Path.Combine(DataFolder, "ai-e2e"));
            return e2ePack == null ? null : new AiManager(DataFolder, () => s.Ai, e2ePack.ReleaseUrl, E2E.TinyAiPack.AssetName);
        }
        return new AiManager(DataFolder, () => s.Ai);
    }
}
