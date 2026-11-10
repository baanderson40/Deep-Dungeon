using BossMod.Autorotation;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;

namespace BossMod.Global.DeepDungeon;

[ConfigDisplay(Name = "Auto-DeepDungeon (Experimental)", Parent = typeof(ModuleConfig))]
public sealed class AutoDDConfig : ConfigNode, IAutoDungeonAutorotationConfig
{
    public enum ClearBehavior
    {
        [PropertyDisplay("Do not auto target")]
        None,
        [PropertyDisplay("Stop when passage opens")]
        Passage,
        [PropertyDisplay("Target everything if not at level cap, otherwise stop when passage opens")]
        Leveling,
        [PropertyDisplay("Target everything")]
        All,
    }

    [PropertyDisplay("Enable module", tooltip: "This is experimental. Use at your own risk. You will sometimes need to help the AI to navigate.")]
    public bool Enable = true;
    [PropertyDisplay("Automatically activate Dungeon AI", tooltip: "Automatically selects the built-in Dungeon AI preset while inside a Deep Dungeon. Manual preset changes suspend automatic control until you leave.")]
    public bool AutoActivateDungeonAI;
    [PropertyDisplay("Automatically leave completely after boss defeat", tooltip: "Opt-in. Requires a supported VBM boss encounter to be observed alive and then dead. Leaves the boss floor, then uses vnav to approach the verified PotD or HoH waiting-room Exit and interacts with it.")]
    public bool AutoLeaveAfterBossDefeat;
    bool IAutoDungeonAutorotationConfig.AutoActivateDungeonAI
    {
        get => AutoActivateDungeonAI;
        set => AutoActivateDungeonAI = value;
    }
    bool IAutoDungeonAutorotationConfig.AutoLeaveAfterBossDefeat
    {
        get => AutoLeaveAfterBossDefeat;
        set => AutoLeaveAfterBossDefeat = value;
    }
    [PropertyDisplay("Enable minimap")]
    public bool EnableMinimap = true;
    [PropertyDisplay("Minimap scale")]
    [PropertySlider(0.2f, 3)]
    public float MinimapScale = 1.0f;
    [PropertyDisplay("Try to avoid traps", tooltip: "Avoid known trap locations sourced from Palantir data. (Traps revealed by a Pomander of Sight will always be avoided.)")]
    public bool TrapHints = true;
    [PropertyDisplay("Draw potential trap locations in game world")]
    public bool DrawTraps = false;

    [PropertyDisplay("Open chests")]
    public bool OpenTreasure = true;

    [PropertyDisplay("Automatic mob targeting behavior")]
    public ClearBehavior AutoClear = ClearBehavior.Leveling;

    [PropertyDisplay("Disable DoTs on non-boss floors (only affects VBM autorotation)")]
    public bool ForbidDOTs = false;

    [PropertyDisplay("Max number of mobs to pull before pausing navigation (0 = do not navigate while in combat)")]
    [PropertySlider(0, 15)]
    public int MaxPull = 0;
    public bool AutoWitching;
    public int AutoWitchingPullThreshold = 4;
    [PropertyDisplay("Try to use terrain to LOS attacks", tooltip: "This feature is currently very inconsistent and will likely get you killed. Enabling it is not recommended.")]
    public bool AutoLOS = false;

    [PropertyDisplay("Reveal all rooms before proceeding to next floor")]
    public bool FullClear = false;

    public BitMask AutoPoms;
    public BitMask AutoMagicite;
    public BitMask AutoDemiclone;
    public bool MaintainStrength;
    public bool MaintainSteel;
    public bool MaintainRaising;
    public bool AutoSafety;
    public bool AutoSight;
    public bool AutoSerenity;
    public bool SerenityOnItemPenalty = true;
    public bool SerenityOnAmnesia = true;
    public bool SerenityOnGloom;
    public bool SerenityOnDamageDown = true;
    public bool SerenityOnHPPenalty = true;
    public bool SerenityOnAutoHealPenalty = true;
    public bool AutoAffluence;
    public bool AutoFlight;
    public bool AutoFortune;
    public bool AutoIntuition;
    public bool AutoFrailty;
    public bool AutoPetrification;

    private readonly record struct PomanderPolicy(PomanderID Icon, Func<bool> Enabled, Action<bool> SetEnabled, string Tooltip);

    public override void DrawCustom(string? page, UITree tree, WorldState ws, RotationDatabase? db)
    {
        UIMisc.SectionHeader("Auto Pom — Use Policies");
        {
            using var indent = ImRaii.PushIndent(ImGui.GetFrameHeight());
            ImGui.Spacing();
            ImGui.TextWrapped("Selected icons enable automatic-use policies. These settings are independent from overcap-use selections below.");
            ImGui.Spacing();

            DrawPomanderPolicySection("Shared: Palace of the Dead / Heaven-on-High / Eureka Orthos", [
                new(PomanderID.Strength, () => MaintainStrength, value => MaintainStrength = value, "Maintain Strength; reserve one charge on normal floors, allow last charge on boss-floor arrival. EO uses Proto-Strength."),
                new(PomanderID.Steel, () => MaintainSteel, value => MaintainSteel = value, "Maintain Steel; reserve one charge on normal floors, allow last charge on boss-floor arrival. EO uses Proto-Steel."),
                new(PomanderID.Raising, () => MaintainRaising, value => MaintainRaising = value, "Maintain Raising protection; may spend last charge. EO uses Proto-Raising."),
                new(PomanderID.Safety, () => AutoSafety, value => AutoSafety = value, "Use once during the first 45 seconds of an initialized non-boss floor, even if last charge. EO uses Proto-Safety."),
                new(PomanderID.Sight, () => AutoSight, value => AutoSight = value, "Floor-start fallback when Safety unavailable; never use both automatically on one floor. EO uses Proto-Sight."),
                new(PomanderID.Serenity, () => AutoSerenity, value => AutoSerenity = value, "Use for selected floor effects; removes beneficial and harmful enchantments. EO uses Proto-Serenity."),
                new(PomanderID.Affluence, () => AutoAffluence, value => AutoAffluence = value, "Use before eligible floor transitions; may spend last charge. EO uses Proto-Affluence."),
                new(PomanderID.Flight, () => AutoFlight, value => AutoFlight = value, "Use before eligible floor transitions; may spend last charge. EO uses Proto-Flight."),
                new(PomanderID.Fortune, () => AutoFortune, value => AutoFortune = value, "Use at floor start when its effect is inactive; may spend last charge. EO uses Proto-Fortune."),
                new(PomanderID.Intuition, () => AutoIntuition, value => AutoIntuition = value, "Use once per normal floor if detection is inactive and hoard not found. EO uses Proto-Intuition."),
                new(PomanderID.Witching, () => AutoWitching, value => AutoWitching = value, "Use on large pulls; one proposal per pull, last charge allowed, solo-only. EO uses Proto-Witching."),
                new(PomanderID.ProtoStrength, () => MaintainStrength, value => MaintainStrength = value, "Uses the shared Strength maintenance policy."),
                new(PomanderID.ProtoSteel, () => MaintainSteel, value => MaintainSteel = value, "Uses the shared Steel maintenance policy."),
                new(PomanderID.ProtoRaising, () => MaintainRaising, value => MaintainRaising = value, "Uses the shared Raising maintenance policy."),
                new(PomanderID.ProtoSafety, () => AutoSafety, value => AutoSafety = value, "Uses the shared Safety floor-start policy."),
                new(PomanderID.ProtoSight, () => AutoSight, value => AutoSight = value, "Uses the shared Sight fallback policy."),
                new(PomanderID.ProtoSerenity, () => AutoSerenity, value => AutoSerenity = value, "Uses the shared Serenity policy."),
                new(PomanderID.ProtoAffluence, () => AutoAffluence, value => AutoAffluence = value, "Uses the shared Affluence floor-transition policy."),
                new(PomanderID.ProtoFlight, () => AutoFlight, value => AutoFlight = value, "Uses the shared Flight floor-transition policy."),
                new(PomanderID.ProtoFortune, () => AutoFortune, value => AutoFortune = value, "Uses the shared Fortune floor-start policy."),
                new(PomanderID.ProtoIntuition, () => AutoIntuition, value => AutoIntuition = value, "Uses the shared Intuition floor policy."),
                new(PomanderID.ProtoWitching, () => AutoWitching, value => AutoWitching = value, "Uses the shared Witching pull policy."),
            ], DrawSharedAdditionalConditions);
            DrawPomanderPolicySection("Heaven-on-High##policy", [
                new(PomanderID.Frailty, () => AutoFrailty, value => AutoFrailty = value, "Use once per eligible normal floor when Petrification is unavailable; may spend last charge."),
                new(PomanderID.Petrification, () => AutoPetrification, value => AutoPetrification = value, "Proactively petrify enemies for floor clearing. Stops when passage opens unless Full Clear is enabled; may spend last charge."),
            ]);
        }

        UIMisc.SectionHeader("Auto Pom — Overcapped (Use When Capped)");
        {
            using var indent = ImRaii.PushIndent(ImGui.GetFrameHeight());
            ImGui.TextWrapped("Selected pomanders may be used to make room when a gold chest contains that pomander and stock is capped. Independent from policies above.");
            ImGui.Spacing();
            DrawPomanderSection("Shared: Palace of the Dead / Heaven-on-High",
                PomanderID.Safety, PomanderID.Sight, PomanderID.Strength, PomanderID.Steel, PomanderID.Affluence,
                PomanderID.Flight, PomanderID.Alteration, PomanderID.Purity, PomanderID.Fortune, PomanderID.Witching,
                PomanderID.Serenity, PomanderID.Intuition, PomanderID.Raising);
            DrawPomanderSection("Palace of the Dead", PomanderID.Rage, PomanderID.Lust, PomanderID.Resolution);
            DrawPomanderSection("Heaven-on-High##overcap", PomanderID.Frailty, PomanderID.Concealment, PomanderID.Petrification);
            DrawPomanderSection("Eureka Orthos: Protomanders",
                PomanderID.ProtoLethargy, PomanderID.ProtoStorms, PomanderID.ProtoDread, PomanderID.ProtoSafety,
                PomanderID.ProtoSight, PomanderID.ProtoStrength, PomanderID.ProtoSteel, PomanderID.ProtoAffluence,
                PomanderID.ProtoFlight, PomanderID.ProtoAlteration, PomanderID.ProtoPurity, PomanderID.ProtoFortune,
                PomanderID.ProtoWitching, PomanderID.ProtoSerenity, PomanderID.ProtoIntuition, PomanderID.ProtoRaising);
            DrawPomanderSection("Pilgrim’s Traverse", PomanderID.Haste, PomanderID.Purification, PomanderID.Devotion);

            ImGui.Spacing();
        }
    }

    private void DrawPomanderPolicySection(string title, PomanderPolicy[] policies, System.Action? drawAdditional = null)
    {
        if (!ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        using var indent = ImRaii.PushIndent(ImGui.GetFrameHeight());
        for (var i = 0; i < policies.Length; ++i)
        {
            var policy = policies[i];
            DrawPomanderIcon(policy.Icon, policy.Enabled(), policy.SetEnabled, policy.Tooltip, $"policy{(int)policy.Icon}");
            if ((i + 1) % 8 != 0 && i + 1 < policies.Length)
                ImGui.SameLine();
        }
        ImGui.Text("");
        ImGui.Spacing();
        drawAdditional?.Invoke();
    }

    private void DrawPomanderSection(string title, params PomanderID[] pomanders)
    {
        if (!ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        using var indent = ImRaii.PushIndent(ImGui.GetFrameHeight());
        for (var i = 0; i < pomanders.Length; ++i)
        {
            var pid = pomanders[i];
            var index = (int)pid;
            DrawPomanderIcon(pid, AutoPoms[index], value => AutoPoms[index] = value,
                Service.LuminaRow<DeepDungeonItem>((uint)pid)!.Value.Name.ToString(), $"overcap{index}");
            if ((i + 1) % 8 != 0 && i + 1 < pomanders.Length)
                ImGui.SameLine();
        }
        ImGui.Text(""); // end icon row
        ImGui.Spacing();
    }

    private void DrawPomanderIcon(PomanderID pid, bool enabled, Action<bool> setEnabled, string tooltip, string id)
    {
        using (ImRaii.PushId(id))
        {
            var row = Service.LuminaRow<DeepDungeonItem>((uint)pid)!.Value;
            var wrap = Service.Texture.GetFromGameIcon(row.Icon).GetWrapOrEmpty();
            var scale = new Vector2(32 * ImGuiHelpers.GlobalScale);
            ImGui.Image(wrap.Handle, scale, new Vector2(0), tintCol: new Vector4(1) with { W = enabled ? 1 : 0.4f });
            if (ImGui.IsItemClicked())
            {
                setEnabled(!enabled);
                Modified.Fire();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"{row.Name}: {tooltip}");
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
        }
    }

    private void DrawSharedAdditionalConditions()
    {
        if (!ImGui.CollapsingHeader("Additional Conditions"))
            return;

        using var indent = ImRaii.PushIndent(ImGui.GetFrameHeight());
        if (AutoSerenity)
        {
            ImGui.TextUnformatted("Serenity triggers:");
            DrawPolicyCheckbox("Item Penalty", ref SerenityOnItemPenalty);
            DrawPolicyCheckbox("Amnesia", ref SerenityOnAmnesia);
            DrawPolicyCheckbox("Gloom", ref SerenityOnGloom);
            DrawPolicyCheckbox("Damage Down", ref SerenityOnDamageDown);
            DrawPolicyCheckbox("HP Penalty", ref SerenityOnHPPenalty);
            DrawPolicyCheckbox("Auto-heal Penalty", ref SerenityOnAutoHealPenalty);
        }
        else
        {
            ImGui.TextDisabled("Enable Serenity above to configure its triggers.");
        }

        if (AutoWitching)
        {
            var threshold = AutoWitchingPullThreshold;
            if (ImGui.SliderInt("Witching pull threshold", ref threshold, 1, 15))
            {
                AutoWitchingPullThreshold = threshold;
                Modified.Fire();
            }
        }
        else
        {
            ImGui.TextDisabled("Enable Witching above to configure its pull threshold.");
        }
    }

    private void DrawPolicyCheckbox(string label, ref bool value)
    {
        if (ImGui.Checkbox(label, ref value))
            Modified.Fire();
    }
}
