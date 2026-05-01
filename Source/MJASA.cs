using UnityEngine;
using KSP;
using KSP.UI.Screens;
using System;
using System.IO;
using System.Collections.Generic;
using MuMech;

[KSPAddon(KSPAddon.Startup.Flight, false)]
public class MJASA : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------------

    private const string LOG_PATH      = "GameData/MJASA/mjasa-archive.csv";
    private const string SETTINGS_PATH = "GameData/MJASA/settings.cfg";
    private const string TOOLBAR_ICON  = "MJASA/toolbar_icon"; // 38x38 PNG in GameData/MJASA/

    private static readonly string CSV_HEADER =
        "kspTime,vesselName,universalTime,missionTitle,notes," +
        "turnStartAltitude,turnStartVelocity,turnEndAltitude,turnEndAngle,turnShapeExponent," +
        "orbitAltitude,autostage,stageLimit,preStageDelay,postStageDelay," +
        "hotstaging,hotstagingLeadTime";

    // -------------------------------------------------------------------------
    // UI State
    // -------------------------------------------------------------------------

    private bool showLogger  = false;
    private bool showHistory = false;

    private Rect loggerRect  = new Rect(200, 200, 280, 180);
    private Rect historyRect = new Rect(80, 80, 780, 420);

    private string  missionTitle = "";
    private string  notes        = "";
    private Vector2 loggerScroll = Vector2.zero;

    // -------------------------------------------------------------------------
    // Settings
    // -------------------------------------------------------------------------

    private bool autoOpenOnLaunch = false;

    // -------------------------------------------------------------------------
    // Toolbar
    // -------------------------------------------------------------------------

    private ApplicationLauncherButton toolbarButton = null;

    // -------------------------------------------------------------------------
    // History State
    // -------------------------------------------------------------------------

    private List<string[]> logEntries    = new List<string[]>();
    private Vector2        historyScroll = Vector2.zero;

    private enum Tab { Mission, TurnPath, Staging }
    private Tab activeTab = Tab.Mission;

    private int pendingDeleteIndex = -1;

    // -------------------------------------------------------------------------
    // Column layout per tab
    // -------------------------------------------------------------------------

    // CSV column index reference (0-based, matches CSV_HEADER order):
    //  0 kspTime            5 turnStartAltitude   10 orbitAltitude    15 hotstaging
    //  1 vesselName          6 turnStartVelocity   11 autostage         16 hotstagingLeadTime
    //  2 universalTime       7 turnEndAltitude     12 stageLimit
    //  3 missionTitle        8 turnEndAngle        13 preStageDelay
    //  4 notes               9 turnShapeExponent   14 postStageDelay

    private static readonly int[] MISSION_COLS   = { 0, 1, 3, 4 };
    private static readonly int[] TURNPATH_COLS  = { 0, 1, 10, 5, 6, 7, 8, 9 };
    private static readonly int[] STAGING_COLS   = { 0, 1, 12, 13, 14, 15, 16 };

    private static readonly string[] MISSION_LABELS  = { "KSP time", "Vessel", "Mission title", "Notes" };
    private static readonly string[] TURNPATH_LABELS = { "KSP time", "Vessel", "Orbit alt", "Start alt", "Start vel", "End alt", "End angle", "Shape %" };
    private static readonly string[] STAGING_LABELS  = { "KSP time", "Vessel", "Stop at", "Pre delay", "Post delay", "Hotstaging", "Lead time" };

    private static readonly int[] MISSION_WIDTHS  = { 150, 110, 120, 230 };
    private static readonly int[] TURNPATH_WIDTHS = { 150, 100,  75,  65,  65,  65,  70,  65 };
    private static readonly int[] STAGING_WIDTHS  = { 150, 110,  70,  70,  70,  75,  70 };

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    void Start()
    {
        LoadSettings();
        AddToolbarButton();

        if (autoOpenOnLaunch)
        {
            showLogger = true;
            toolbarButton?.SetTrue(makeCall: false);
        }
    }

    void OnDestroy()
    {
        if (toolbarButton != null)
            ApplicationLauncher.Instance.RemoveModApplication(toolbarButton);
    }

    // -------------------------------------------------------------------------
    // Toolbar
    // -------------------------------------------------------------------------

    void AddToolbarButton()
    {
        if (ApplicationLauncher.Instance == null) return;

        Texture2D icon = GameDatabase.Instance.GetTexture(TOOLBAR_ICON, false);

        toolbarButton = ApplicationLauncher.Instance.AddModApplication(
            onTrue:          OnToolbarOpen,
            onFalse:         OnToolbarClose,
            onHover:         null,
            onHoverOut:      null,
            onEnable:        null,
            onDisable:       null,
            visibleInScenes: ApplicationLauncher.AppScenes.FLIGHT,
            texture:         icon
        );
    }

    void OnToolbarOpen()  { showLogger = true; }

    void OnToolbarClose()
    {
        showLogger         = false;
        showHistory        = false;
        pendingDeleteIndex = -1;
    }

    // -------------------------------------------------------------------------
    // Settings persistence
    // -------------------------------------------------------------------------

    void LoadSettings()
    {
        string path = KSPUtil.ApplicationRootPath + SETTINGS_PATH;
        if (!File.Exists(path)) return;

        foreach (string line in File.ReadAllLines(path))
        {
            string[] parts = line.Split('=');
            if (parts.Length != 2) continue;

            string key   = parts[0].Trim();
            string value = parts[1].Trim();

            if (key == "autoOpenOnLaunch")
                bool.TryParse(value, out autoOpenOnLaunch);
        }
    }

    void SaveSettings()
    {
        string path = KSPUtil.ApplicationRootPath + SETTINGS_PATH;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, $"autoOpenOnLaunch = {autoOpenOnLaunch.ToString().ToLower()}\n");
    }

    // -------------------------------------------------------------------------
    // GUI
    // -------------------------------------------------------------------------

    void OnGUI()
    {
        if (showLogger)
            loggerRect = GUILayout.Window(123456, loggerRect, DrawLoggerWindow, "MechJeb Ascent & Staging Archive");

        if (showHistory)
            historyRect = GUILayout.Window(123457, historyRect, DrawHistoryWindow, "MJASA — Archive");
    }

    void DrawLoggerWindow(int id)
    {
        GUILayout.BeginVertical();

        GUILayout.Label("Mission Title:");
        missionTitle = GUILayout.TextField(missionTitle);

        GUILayout.Space(5);

        GUILayout.Label("Notes:");
        loggerScroll = GUILayout.BeginScrollView(loggerScroll, GUILayout.Height(60));
        notes = GUILayout.TextArea(notes, GUILayout.ExpandHeight(true));
        GUILayout.EndScrollView();

        GUILayout.Space(5);

        if (GUILayout.Button("Archive MechJeb Settings"))
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel != null)
                LogLaunch(vessel);
        }

        GUILayout.Space(5);

        if (GUILayout.Button(showHistory ? "Hide Archive" : "Show Archive"))
        {
            showHistory = !showHistory;
            if (showHistory)
                LoadHistory();
        }

        GUILayout.Space(8);


        if (GUILayout.Button("Close"))
        {
            showLogger  = false;
            showHistory = false;
            toolbarButton?.SetFalse(makeCall: false);
        }
        GUILayout.Space(3);

        bool newAutoOpen = GUILayout.Toggle(autoOpenOnLaunch, " Auto-open on scene load");
        if (newAutoOpen != autoOpenOnLaunch)
        {
            autoOpenOnLaunch = newAutoOpen;
            SaveSettings();
        }


        GUILayout.EndVertical();
        GUI.DragWindow();
    }

    void DrawHistoryWindow(int id)
    {
        GUILayout.BeginVertical();

        // -- Tab bar --
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(activeTab == Tab.Mission,  " Mission ",  GUI.skin.button)) activeTab = Tab.Mission;
        if (GUILayout.Toggle(activeTab == Tab.TurnPath, " Turn path", GUI.skin.button)) activeTab = Tab.TurnPath;
        if (GUILayout.Toggle(activeTab == Tab.Staging,  " Staging ",  GUI.skin.button)) activeTab = Tab.Staging;
        GUILayout.EndHorizontal();

        GUILayout.Space(4);

        if (logEntries.Count == 0)
        {
            GUILayout.Label("No archived entries found.");
        }
        else
        {
            GUILayout.Label($"{logEntries.Count} entr{(logEntries.Count == 1 ? "y" : "ies")} archived — newest first");
            GUILayout.Space(4);

            int[]    cols      = ActiveCols();
            string[] labels    = ActiveLabels();
            int[]    widths    = ActiveWidths();
            bool     isMission = activeTab == Tab.Mission;

            historyScroll = GUILayout.BeginScrollView(historyScroll);

            // Header row
            GUILayout.BeginHorizontal();
            for (int c = 0; c < labels.Length; c++)
                GUILayout.Label($"<b>{labels[c]}</b>", GUILayout.Width(widths[c]));
            if (isMission)
                GUILayout.Label("<b></b>", GUILayout.Width(72));
            GUILayout.EndHorizontal();

            GUILayout.Space(2);

            int deleteTarget = -1;

            for (int r = logEntries.Count - 1; r >= 0; r--)
            {
                string[] row             = logEntries[r];
                bool     isPendingDelete = pendingDeleteIndex == r;

                GUILayout.BeginHorizontal(isPendingDelete ? GUI.skin.box : GUIStyle.none);

                for (int c = 0; c < cols.Length; c++)
                {
                    int    colIdx = cols[c];
                    string cell   = colIdx < row.Length ? row[colIdx] : "";

                    if (double.TryParse(cell,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double num))
                        cell = num.ToString("0.##");

                    GUILayout.Label(cell, GUILayout.Width(widths[c]));
                }

                if (isMission)
                {
                    if (isPendingDelete)
                    {
                        if (GUILayout.Button("Del", GUILayout.Width(36))) deleteTarget = r;
                        if (GUILayout.Button("No",  GUILayout.Width(36))) pendingDeleteIndex = -1;
                    }
                    else
                    {
                        if (GUILayout.Button("Delete", GUILayout.Width(72)))
                            pendingDeleteIndex = r;
                    }
                }

                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();

            if (deleteTarget >= 0)
            {
                logEntries.RemoveAt(deleteTarget);
                pendingDeleteIndex = -1;
                SaveAllEntries();
            }
        }

        GUILayout.Space(6);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Refresh")) { pendingDeleteIndex = -1; LoadHistory(); }
        if (GUILayout.Button("Close"))   { pendingDeleteIndex = -1; showHistory = false; }
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUI.DragWindow();
    }

    // -------------------------------------------------------------------------
    // Tab helpers
    // -------------------------------------------------------------------------

    int[]    ActiveCols()   => activeTab == Tab.Mission ? MISSION_COLS   : activeTab == Tab.TurnPath ? TURNPATH_COLS   : STAGING_COLS;
    string[] ActiveLabels() => activeTab == Tab.Mission ? MISSION_LABELS : activeTab == Tab.TurnPath ? TURNPATH_LABELS : STAGING_LABELS;
    int[]    ActiveWidths() => activeTab == Tab.Mission ? MISSION_WIDTHS : activeTab == Tab.TurnPath ? TURNPATH_WIDTHS : STAGING_WIDTHS;

    // -------------------------------------------------------------------------
    // Core Logging
    // -------------------------------------------------------------------------

    void LogLaunch(Vessel vessel)
    {
        try
        {
            var mjCore = vessel.FindPartModuleImplementing<MuMech.MechJebCore>();
            if (mjCore == null) { Debug.Log("[MJASA] No MechJebCore found on active vessel."); return; }

            var ascent = mjCore.GetComputerModule<MechJebModuleAscentSettings>();
            if (ascent == null) { Debug.Log("[MJASA] No Ascent Settings module found."); return; }

            var staging = mjCore.GetComputerModule<MechJebModuleStagingController>();
            if (staging == null) { Debug.Log("[MJASA] No Staging Controller module found."); return; }

            double ut = Planetarium.GetUniversalTime();

            LaunchData data = new LaunchData
            {
                kspTime            = KSPUtil.PrintDateCompact(ut, includeTime: true, includeSeconds: true),
                vesselName         = vessel.vesselName,
                universalTime      = ut,
                missionTitle       = missionTitle,
                notes              = notes,

                turnStartAltitude  = ascent.AutoTurnStartAltitude,
                turnStartVelocity  = ascent.AutoTurnStartVelocity,
                turnEndAltitude    = ascent.AutoTurnEndAltitude,
                turnEndAngle       = ascent.TurnEndAngle,
                turnShapeExponent  = ascent.TurnShapeExponent * 100,

                orbitAltitude      = ascent.DesiredOrbitAltitude,
                autostage          = ascent._autostage,

                stageLimit         = staging.AutostageLimit.ValConfig,
                preStageDelay      = staging.AutostagePreDelay.ValConfig,
                postStageDelay     = staging.AutostagePostDelay.ValConfig,
                hotstaging         = staging.HotStaging,
                hotstagingLeadTime = staging.HotStagingLeadTime.ValConfig,
            };

            AppendCsvRow(data);
            missionTitle = "";
            notes        = "";

            if (showHistory) LoadHistory();

            ScreenMessages.PostScreenMessage("[MJASA] Settings archived.", 3f, ScreenMessageStyle.UPPER_CENTER);
            Debug.Log("[MJASA] MechJeb settings archived successfully.");
        }
        catch (Exception e)
        {
            Debug.LogError("[MJASA] Error while archiving settings: " + e);
        }
    }

    // -------------------------------------------------------------------------
    // CSV Read / Write
    // -------------------------------------------------------------------------

    void AppendCsvRow(LaunchData d)
    {
        string path = KSPUtil.ApplicationRootPath + LOG_PATH;
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        bool needsHeader = !File.Exists(path);

        using (StreamWriter sw = new StreamWriter(path, append: true))
        {
            if (needsHeader)
                sw.WriteLine(CSV_HEADER);

            sw.WriteLine(string.Join(",",
                CsvCell(d.kspTime),
                CsvCell(d.vesselName),
                d.universalTime.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                CsvCell(d.missionTitle),
                CsvCell(d.notes),
                F(d.turnStartAltitude),
                F(d.turnStartVelocity),
                F(d.turnEndAltitude),
                F(d.turnEndAngle),
                F(d.turnShapeExponent),
                F(d.orbitAltitude),
                d.autostage.ToString().ToLower(),
                F(d.stageLimit),
                F(d.preStageDelay),
                F(d.postStageDelay),
                d.hotstaging.ToString().ToLower(),
                F(d.hotstagingLeadTime)
            ));
        }
    }

    void LoadHistory()
    {
        logEntries.Clear();
        string path = KSPUtil.ApplicationRootPath + LOG_PATH;

        if (!File.Exists(path)) { Debug.Log("[MJASA] No archive file found at: " + path); return; }

        string[] lines = File.ReadAllLines(path);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (!string.IsNullOrEmpty(line))
                logEntries.Add(ParseCsvLine(line));
        }

        Debug.Log($"[MJASA] Loaded {logEntries.Count} archived entries.");
    }

    void SaveAllEntries()
    {
        string path = KSPUtil.ApplicationRootPath + LOG_PATH;
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        using (StreamWriter sw = new StreamWriter(path, append: false))
        {
            sw.WriteLine(CSV_HEADER);
            foreach (string[] row in logEntries)
                sw.WriteLine(string.Join(",", System.Array.ConvertAll(row, CsvCell)));
        }

        Debug.Log($"[MJASA] Archive rewritten with {logEntries.Count} entries.");
    }

    string[] ParseCsvLine(string line)
    {
        var  fields   = new List<string>();
        bool inQuotes = false;
        var  current  = new System.Text.StringBuilder();

        foreach (char c in line)
        {
            if (c == '"')                   inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes) { fields.Add(current.ToString()); current.Clear(); }
            else                            current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }

    string CsvCell(string value)
    {
        if (value == null) value = "";
        value = value.Replace("\r", "").Replace("\n", " ");
        if (value.Contains(",") || value.Contains("\""))
            value = "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    string F(double value) =>
        value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

    // -------------------------------------------------------------------------
    // Data Model
    // -------------------------------------------------------------------------

    private class LaunchData
    {
        public string kspTime;
        public string vesselName;
        public double universalTime;
        public string missionTitle;
        public string notes;

        public double turnStartAltitude;
        public double turnStartVelocity;
        public double turnEndAltitude;
        public double turnEndAngle;
        public double turnShapeExponent;

        public double orbitAltitude;
        public bool   autostage;
        public double stageLimit;
        public double preStageDelay;
        public double postStageDelay;
        public bool   hotstaging;
        public double hotstagingLeadTime;
    }
}