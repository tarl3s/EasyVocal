using System.IO;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;

namespace EasyVocal;

/// <summary>Which AI voices a session gets: the data folder's (normal runs), a stand-in (smoke test), a test pack (e2e).</summary>
internal sealed partial class AppSession
{
    private StandInAi? smokeAi;
    private E2E.E2EAiPack? e2ePack;

    private IVoiceConverterSource? MakeAiVoices(AppSettings s)
    {
        if (Args.SmokeTest) return smokeAi = new StandInAi();
        if (Args.E2E)
        {
            // Only with AI test models: the tiny ones (the headless tests pass them) or a real pack's zip (--e2e-ai-pack);
            // a packaged run with neither skips the AI steps.
            if (Args.E2ERelaunch) return null;
            e2ePack = E2E.E2EAiPack.TryCreate(options.AiFixtures ?? Args.AiFixtures, Args.AiPackZip, Path.Combine(DataFolder, "ai-e2e"));
            return e2ePack == null ? null : new AiManager(DataFolder, () => s.Ai, e2ePack.ReleaseUrl, e2ePack.AssetName);
        }
        return new AiManager(DataFolder, () => s.Ai);
    }
}
