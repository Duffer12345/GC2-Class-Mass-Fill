#if UNITY_EDITOR

using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal; // For ActiveEditorTracker and InternalEditorUtility
using UnityEngine;

// Alias the GC2 runtime types so we don’t fight with the "class" keyword:
using GC2Class = GameCreator.Runtime.Stats.Class;
using Stat = GameCreator.Runtime.Stats.Stat;
using Attribute = GameCreator.Runtime.Stats.Attribute;

//
// ClassBulkTraitAssignerWindow
// ============================
//
// A custom EditorWindow for Game Creator 2 – Stats 2 that lets you
// bulk-assign Stat and Attribute assets to a GC2 Class asset.
//
// CORE IDEA
// ---------
// - You select a GC2 Class asset.
// - You can:
//     • Drag one or more Stat assets into a staging list.
//     • Drag one or more Attribute assets into another staging list.
//     • OR pick a Project folder and auto-scan it (and subfolders) for
//       all Stat and Attribute assets.
// - Click "Add To Class".
// - The window will:
//
//   • Use reflection to access the private fields on GC2 Class:
//       m_Stats      : StatList
//       m_Attributes : AttributeList
//
//   • Inside those wrapper types, access their internal collections:
//       StatList.m_Stats          : StatItem[]  (or List<StatItem>)
//       AttributeList.m_Attributes: AttributeItem[] (or List<AttributeItem>)
//
//   • Create proper StatItem / AttributeItem instances (using GC2 types).
//   • Set m_Stat or m_Attribute to the asset you dragged/found.
//   • Leave all other fields at default values (matching GC2's own UI).
//   • Append them to the internal lists if they are not already present.
//
// IMPORTANT
// ---------
// We *never* mutate the internal arrays/lists via SerializedProperty,
// because GC2's polymorphic editor tools expect a specific layout and
// can break if the serialized data is shaped incorrectly.
//
// Instead, we:
//   - Manipulate the actual object graph via reflection.
//   - Then let Unity serialize that object graph normally.
//
// This version also correctly handles the case where GC2 stores the
// internal collections as **arrays** (fixed-size). When that happens,
// we allocate a new array of size+1, copy elements over, append the new
// item, and assign it back to the field.
//
// NEW IN THIS VERSION
// -------------------
// 1) "Folder Scan" support:
//    - You can choose a folder in the Project and, with one button,
//      auto-stage all Stat and Attribute assets under that folder.
//
// 2) Inspector auto-refresh:
//    - After adding traits, the tool forces the Inspector to rebuild
//      and repaint so that the selected Class asset immediately shows
//      the new Stats/Attributes, without you needing to click away and
//      back onto the asset.
//

public class ClassBulkTraitAssignerWindow : EditorWindow
{
    // ------------------------------------------------------------------
    // FIELDS & STATE
    // ------------------------------------------------------------------

    /// <summary>
    /// The GC2 Class asset we want to populate.
    /// </summary>
    private GC2Class _targetClass;

    /// <summary>
    /// Staging lists: what the user has dragged or auto-detected,
    /// waiting to be added to the Class.
    ///
    /// These are NOT directly tied to the Class until the user clicks
    /// "Add To Class". You can safely clear or change them in the UI.
    /// </summary>
    [SerializeField] private List<Stat> _statsToAdd = new();
    [SerializeField] private List<Attribute> _attributesToAdd = new();

    /// <summary>
    /// Scroll position for the main scroll view.
    /// </summary>
    private Vector2 _scrollPos;

    // ------------------------------------------------------------------
    // FOLDER-SCAN RELATED FIELDS
    // ------------------------------------------------------------------

    /// <summary>
    /// Folder in the Project that we can scan for Stats and Attributes.
    /// This is stored as DefaultAsset (Unity's generic asset type), but
    /// must refer to a folder path in the AssetDatabase.
    /// </summary>
    [SerializeField] private DefaultAsset _scanFolder;

    /// <summary>
    /// If true, when we scan the folder we will first clear the staged
    /// Stats and Attributes lists before adding new ones from the scan.
    /// If false, scanned assets are *appended* to whatever is already
    /// staged (avoiding duplicates).
    /// </summary>
    [SerializeField] private bool _clearStagedBeforeScan = true;

    // ------------------------------------------------------------------
    // MENU ENTRY
    // ------------------------------------------------------------------

    [MenuItem("Tools/Game Creator 2/Stats/Class Bulk Trait Assigner")]
    private static void OpenWindow()
    {
        var window = GetWindow<ClassBulkTraitAssignerWindow>();
        window.titleContent = new GUIContent("Class Bulk Traits");
        window.minSize = new Vector2(500f, 350f);
        window.Show();
    }

    // ------------------------------------------------------------------
    // GUI
    // ------------------------------------------------------------------

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Game Creator 2 – Stats 2", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Class Bulk Trait Assigner", EditorStyles.largeLabel);
        EditorGUILayout.Space();

        EditorGUILayout.HelpBox(
            "1. Assign a GC2 Class asset.\n" +
            "2. Optionally select a folder and scan for all Stats/Attributes.\n" +
            "3. Or drag Stat and Attribute assets into the panels.\n" +
            "4. Click 'Add To Class' to append them (no duplicates, existing entries preserved).",
            MessageType.Info
        );

        EditorGUILayout.Space();
        DrawTargetClassField();
        EditorGUILayout.Space();

        // NEW: Folder scan section
        DrawFolderScanSection();

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(_targetClass == null))
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            DrawStatsDropArea();
            EditorGUILayout.Space(8f);
            DrawAttributesDropArea();

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            DrawButtons();
        }
    }

    // ------------------------------------------------------------------
    // TARGET CLASS FIELD
    // ------------------------------------------------------------------

    private void DrawTargetClassField()
    {
        EditorGUILayout.LabelField("Target Class Asset", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        _targetClass = (GC2Class)EditorGUILayout.ObjectField(
            new GUIContent(
                "Class",
                "The Game Creator 2 'Class' ScriptableObject you want to populate."
            ),
            _targetClass,
            typeof(GC2Class),
            false // asset only
        );
        if (EditorGUI.EndChangeCheck())
        {
            // We *could* clear staged lists on Class change,
            // but keeping them is often useful if you want
            // to assign the same Stat/Attribute set to multiple Classes.
        }

        if (_targetClass == null)
        {
            EditorGUILayout.HelpBox(
                "Assign a GC2 Class asset to enable bulk adding.",
                MessageType.Warning
            );
        }
    }

    // ------------------------------------------------------------------
    // FOLDER SCAN SECTION (NEW)
    // ------------------------------------------------------------------

    /// <summary>
    /// UI for selecting a Project folder and scanning it (and subfolders)
    /// for Stat and Attribute assets.
    /// </summary>
    private void DrawFolderScanSection()
    {
        EditorGUILayout.LabelField("Folder Scan (Optional)", EditorStyles.boldLabel);

        EditorGUILayout.HelpBox(
            "Select a folder in your Project, then click 'Scan Folder' to " +
            "automatically stage all Stat and Attribute assets found under " +
            "that folder and its subfolders.",
            MessageType.None
        );

        EditorGUILayout.BeginHorizontal();
        _scanFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent(
                "Folder",
                "Project folder to scan. Must be a folder asset (not a file)."
            ),
            _scanFolder,
            typeof(DefaultAsset),
            false
        );

        _clearStagedBeforeScan = EditorGUILayout.ToggleLeft(
            new GUIContent(
                "Clear staged before scan",
                "If enabled, existing staged Stats/Attributes are cleared " +
                "before adding the scanned ones."
            ),
            _clearStagedBeforeScan,
            GUILayout.Width(180f)
        );

        EditorGUILayout.EndHorizontal();

        using (new EditorGUI.DisabledScope(_scanFolder == null))
        {
            if (GUILayout.Button(
                    new GUIContent(
                        "Scan Folder for Stats & Attributes",
                        "Searches the selected folder (and all subfolders) for " +
                        "Stat and Attribute ScriptableObjects and stages them."
                    ),
                    GUILayout.Height(22f)))
            {
                ScanFolderForTraits();
            }
        }
    }

    /// <summary>
    /// Scans the selected Project folder (and its subfolders) for assets
    /// that are Stats or Attributes, and stages them in the _statsToAdd
    /// and _attributesToAdd lists.
    ///
    /// Implementation notes:
    /// ---------------------
    /// - We retrieve the folder path via AssetDatabase.GetAssetPath.
    /// - We verify it's actually a folder via AssetDatabase.IsValidFolder.
    /// - We then use AssetDatabase.FindAssets with a broad filter
    ///   (t:ScriptableObject) and manually filter loaded assets by type
    ///   (Stat or Attribute). This is robust and avoids guessing type
    ///   names for the search string.
    /// </summary>
    private void ScanFolderForTraits()
    {
        if (_scanFolder == null)
        {
            Debug.LogWarning("[ClassBulkTraitAssigner] No folder selected to scan.");
            return;
        }

        string folderPath = AssetDatabase.GetAssetPath(_scanFolder);

        if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
        {
            Debug.LogError(
                $"[ClassBulkTraitAssigner] Selected asset '{_scanFolder.name}' is not a valid folder."
            );
            return;
        }

        if (_clearStagedBeforeScan)
        {
            _statsToAdd.Clear();
            _attributesToAdd.Clear();
        }

        // Find all ScriptableObjects in this folder and subfolders.
        string[] guids = AssetDatabase.FindAssets("t:ScriptableObject", new[] { folderPath });

        // TEMP collections that track both the asset and its path so we can sort.
        var foundStats = new List<(Stat stat, string path)>();
        var foundAttributes = new List<(Attribute attribute, string path)>();

        foreach (string guid in guids)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(assetPath)) continue;

            Object obj = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
            if (obj == null) continue;

            if (obj is Stat statAsset)
            {
                foundStats.Add((statAsset, assetPath));
            }
            else if (obj is Attribute attributeAsset)
            {
                foundAttributes.Add((attributeAsset, assetPath));
            }
        }

        // Sort so they are effectively grouped by folder, then by file name.
        foundStats.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
        foundAttributes.Sort((a, b) => string.CompareOrdinal(a.path, b.path));

        int newlyStagedStats = 0;
        int newlyStagedAttributes = 0;

        // Stage in sorted order, skipping duplicates.
        foreach (var entry in foundStats)
        {
            if (!_statsToAdd.Contains(entry.stat))
            {
                _statsToAdd.Add(entry.stat);
                newlyStagedStats++;
            }
        }

        foreach (var entry in foundAttributes)
        {
            if (!_attributesToAdd.Contains(entry.attribute))
            {
                _attributesToAdd.Add(entry.attribute);
                newlyStagedAttributes++;
            }
        }

        Debug.Log(
            $"[ClassBulkTraitAssigner] Folder scan complete. " +
            $"Staged {newlyStagedStats} Stat(s) and {newlyStagedAttributes} Attribute(s) from '{folderPath}', " +
            $"ordered by sub-folder and asset name."
        );
    }



    // ------------------------------------------------------------------
    // DRAG & DROP AREAS
    // ------------------------------------------------------------------

    private void DrawStatsDropArea()
    {
        EditorGUILayout.LabelField("Stats to Add", EditorStyles.boldLabel);

        Rect dropArea = GUILayoutUtility.GetRect(0f, 60f, GUILayout.ExpandWidth(true));

        GUI.Box(
            dropArea,
            "Drag Stat assets here\n(you can drag multiple from the Project window)",
            EditorStyles.helpBox
        );

        HandleDragAndDrop(dropArea, isStatArea: true);

        // Show current staged Stats
        if (_statsToAdd.Count > 0)
        {
            EditorGUILayout.LabelField("Staged Stats:", EditorStyles.miniBoldLabel);

            for (int i = 0; i < _statsToAdd.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                _statsToAdd[i] = (Stat)EditorGUILayout.ObjectField(
                    _statsToAdd[i],
                    typeof(Stat),
                    false
                );

                if (GUILayout.Button("X", GUILayout.Width(22f)))
                {
                    _statsToAdd.RemoveAt(i);
                    i--;
                }

                EditorGUILayout.EndHorizontal();
            }
        }
        else
        {
            EditorGUILayout.LabelField("No Stats staged.", EditorStyles.miniLabel);
        }
    }

    private void DrawAttributesDropArea()
    {
        EditorGUILayout.LabelField("Attributes to Add", EditorStyles.boldLabel);

        Rect dropArea = GUILayoutUtility.GetRect(0f, 60f, GUILayout.ExpandWidth(true));

        GUI.Box(
            dropArea,
            "Drag Attribute assets here\n(you can drag multiple from the Project window)",
            EditorStyles.helpBox
        );

        HandleDragAndDrop(dropArea, isStatArea: false);

        // Show current staged Attributes
        if (_attributesToAdd.Count > 0)
        {
            EditorGUILayout.LabelField("Staged Attributes:", EditorStyles.miniBoldLabel);

            for (int i = 0; i < _attributesToAdd.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                _attributesToAdd[i] = (Attribute)EditorGUILayout.ObjectField(
                    _attributesToAdd[i],
                    typeof(Attribute),
                    false
                );

                if (GUILayout.Button("X", GUILayout.Width(22f)))
                {
                    _attributesToAdd.RemoveAt(i);
                    i--;
                }

                EditorGUILayout.EndHorizontal();
            }
        }
        else
        {
            EditorGUILayout.LabelField("No Attributes staged.", EditorStyles.miniLabel);
        }
    }

    /// <summary>
    /// Shared drag-and-drop handler for the two drop areas.
    /// </summary>
    private void HandleDragAndDrop(Rect dropArea, bool isStatArea)
    {
        Event evt = Event.current;
        if (evt == null) return;

        if (!dropArea.Contains(evt.mousePosition)) return;

        if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();

                foreach (Object dragged in DragAndDrop.objectReferences)
                {
                    if (dragged == null) continue;

                    if (isStatArea && dragged is Stat statAsset)
                    {
                        if (!_statsToAdd.Contains(statAsset))
                            _statsToAdd.Add(statAsset);
                    }
                    else if (!isStatArea && dragged is Attribute attrAsset)
                    {
                        if (!_attributesToAdd.Contains(attrAsset))
                            _attributesToAdd.Add(attrAsset);
                    }
                }
            }

            evt.Use();
        }
    }

    // ------------------------------------------------------------------
    // BUTTONS
    // ------------------------------------------------------------------

    private void DrawButtons()
    {
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button(
                new GUIContent(
                    "Clear Staged Lists",
                    "Clears the in-window lists of Stats and Attributes to add."
                ),
                GUILayout.Height(24f)))
        {
            _statsToAdd.Clear();
            _attributesToAdd.Clear();
        }

        using (new EditorGUI.DisabledScope(
                   _targetClass == null ||
                   (_statsToAdd.Count == 0 && _attributesToAdd.Count == 0)))
        {
            if (GUILayout.Button(
                    new GUIContent(
                        "Add To Class",
                        "Append staged Stats and Attributes to the target Class, " +
                        "preserving existing entries and avoiding duplicates."
                    ),
                    GUILayout.Height(24f)))
            {
                AddStagedTraitsToClass();
            }
        }

        EditorGUILayout.EndHorizontal();
    }

    // ------------------------------------------------------------------
    // CORE LOGIC: WRITE INTO CLASS (REFLECTION)
    // ------------------------------------------------------------------

    /// <summary>
    /// Applies all staged Stats and Attributes into the selected GC2 Class.
    /// Uses reflection to interact with GC2's internal StatList/AttributeList,
    /// avoiding any direct SerializedProperty array hacking.
    /// </summary>
    private void AddStagedTraitsToClass()
    {
        // Safety checks -----------------------------------------------------------
        if (_targetClass == null)
        {
            Debug.LogError(
                "[ClassBulkTraitAssigner] No target Class selected. " +
                "Please assign a GC2 Class asset at the top of the window.");
            return;
        }

        if ((_statsToAdd == null || _statsToAdd.Count == 0) &&
            (_attributesToAdd == null || _attributesToAdd.Count == 0))
        {
            Debug.LogWarning(
                "[ClassBulkTraitAssigner] No Stats or Attributes staged. " +
                "Drag some Stat/Attribute assets into the window or scan a folder first.");
            return;
        }

        // Record Undo so the operation can be reverted from the Unity Undo stack
        Undo.RecordObject(_targetClass, "Add Stats/Attributes to Class");

        int addedStats = 0;
        int addedAttributes = 0;

        // -------------------------------------------------------------------------
        // 1) Add all staged Stats
        // -------------------------------------------------------------------------
        if (_statsToAdd != null)
        {
            foreach (Stat statAsset in _statsToAdd)
            {
                if (statAsset == null) continue;

                bool added = AddStatToClassViaReflection(_targetClass, statAsset);
                if (added) addedStats++;
            }
        }

        // -------------------------------------------------------------------------
        // 2) Add all staged Attributes
        // -------------------------------------------------------------------------
        if (_attributesToAdd != null)
        {
            foreach (Attribute attributeAsset in _attributesToAdd)
            {
                if (attributeAsset == null) continue;

                bool added = AddAttributeToClassViaReflection(_targetClass, attributeAsset);
                if (added) addedAttributes++;
            }
        }

        // Mark the Class asset as dirty so Unity knows it has changed
        EditorUtility.SetDirty(_targetClass);

        // Save the changes to disk
        AssetDatabase.SaveAssets();

        // NEW: Force the Inspector to rebuild & repaint so that if the
        // Class asset is currently selected in the Inspector, you see
        // the newly added Stats/Attributes immediately, without having
        // to click away and back again.
        EditorApplication.delayCall += () =>
        {
            try
            {
                ActiveEditorTracker.sharedTracker.ForceRebuild();
                InternalEditorUtility.RepaintAllViews();
            }
            catch
            {
                // In case Unity internals change, we fail silently rather than
                // breaking the core functionality.
            }
        };

        Debug.Log(
            $"[ClassBulkTraitAssigner] Added {addedStats} Stat(s) and " +
            $"{addedAttributes} Attribute(s) to Class '{_targetClass.name}'.");
    }

    // ------------------------------------------------------------------
    // (LEGACY) DUPLICATE CHECK HELPERS – left in place (not used anymore)
    // ------------------------------------------------------------------

    private static bool ClassAlreadyContainsStat(SerializedProperty statsArrayProp, Stat stat)
    {
        if (statsArrayProp == null || !statsArrayProp.isArray || stat == null)
            return false;

        int count = statsArrayProp.arraySize;
        for (int i = 0; i < count; i++)
        {
            SerializedProperty element = statsArrayProp.GetArrayElementAtIndex(i);
            if (element == null) continue;

            SerializedProperty statRefProp = element.FindPropertyRelative("m_Stat");
            if (statRefProp == null ||
                statRefProp.propertyType != SerializedPropertyType.ObjectReference)
            {
                continue;
            }

            if (statRefProp.objectReferenceValue == stat)
                return true;
        }

        return false;
    }

    private static bool ClassAlreadyContainsAttribute(SerializedProperty attrsArrayProp, Attribute attribute)
    {
        if (attrsArrayProp == null || !attrsArrayProp.isArray || attribute == null)
            return false;

        int count = attrsArrayProp.arraySize;
        for (int i = 0; i < count; i++)
        {
            SerializedProperty element = attrsArrayProp.GetArrayElementAtIndex(i);
            if (element == null) continue;

            SerializedProperty attrRefProp = element.FindPropertyRelative("m_Attribute");
            if (attrRefProp == null ||
                attrRefProp.propertyType != SerializedPropertyType.ObjectReference)
            {
                continue;
            }

            if (attrRefProp.objectReferenceValue == attribute)
                return true;
        }

        return false;
    }

    // ----------------------------
    // REFLECTION UTILITIES
    // ----------------------------

    /// <summary>
    /// BindingFlags used for all our reflection calls:
    /// - Instance: We want instance fields (not static).
    /// - NonPublic: GC2 uses internal/private backing fields like "m_Stats", "m_Attributes".
    /// </summary>
    private static readonly System.Reflection.BindingFlags BIND_FLAGS =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    /// <summary>
    /// Safely adds a Stat to a GC2 Class using reflection and the internal StatList/StatItem types.
    /// Returns true if a new StatItem was actually added; false if it was already present or an error occurred.
    ///
    /// IMPORTANT:
    /// GC2 may store the internal collection as an array (fixed-size).
    /// In that case, calling IList.Add will throw NotSupportedException.
    /// We detect that and grow the array manually.
    /// </summary>
    private static bool AddStatToClassViaReflection(
        GC2Class cls,
        Stat statAsset)
    {
        if (cls == null || statAsset == null) return false;

        // 1) Get private field "m_Stats" from Class (type: StatList)
        var classType = typeof(GC2Class);
        var statsField = classType.GetField("m_Stats", BIND_FLAGS);
        if (statsField == null)
        {
            Debug.LogError(
                "[ClassBulkTraitAssigner] Could not find field 'm_Stats' on Class. " +
                "Game Creator internal layout may have changed.");
            return false;
        }

        object statListObj = statsField.GetValue(cls);
        if (statListObj == null)
        {
            Debug.LogError(
                "[ClassBulkTraitAssigner] Class.m_Stats is null. Cannot add Stat.");
            return false;
        }

        // 2) Inside StatList there is another field "m_Stats" which is the actual collection
        var statListType = statListObj.GetType();
        var innerListField = statListType.GetField("m_Stats", BIND_FLAGS);
        if (innerListField == null)
        {
            Debug.LogError(
                "[ClassBulkTraitAssigner] Could not find inner field 'm_Stats' on StatList. " +
                "Game Creator internal layout may have changed.");
            return false;
        }

        object rawCollection = innerListField.GetValue(statListObj);

        // We treat it as IList for iteration/duplicate check, but it may be a fixed-size array
        var list = rawCollection as System.Collections.IList;
        if (list == null)
        {
            Debug.LogError(
                "[ClassBulkTraitAssigner] StatList.m_Stats is not an IList. " +
                "Game Creator internal layout may have changed.");
            return false;
        }

        // 3) Prevent duplicates: if the Stat is already in the Class, bail out
        foreach (var entry in list)
        {
            if (entry == null) continue;

            var itemType = entry.GetType(); // StatItem type at runtime
            var statFieldInfo = itemType.GetField("m_Stat", BIND_FLAGS);
            if (statFieldInfo == null) continue;

            Stat existing = statFieldInfo.GetValue(entry) as Stat;
            if (existing == statAsset)
            {
                // Already present: do not add a second StatItem with the same Stat
                return false;
            }
        }

        // 4) Create a new StatItem and initialize the internal fields
        System.Type statItemType = typeof(GameCreator.Runtime.Stats.StatItem);
        object newItem = System.Activator.CreateInstance(statItemType);

        // Set the 'm_Stat' field to point to the Stat asset
        var newStatField = statItemType.GetField("m_Stat", BIND_FLAGS);
        if (newStatField != null)
        {
            newStatField.SetValue(newItem, statAsset);
        }

        // Optionally ensure 'm_IsHidden' is false if the field exists
        var isHiddenField = statItemType.GetField("m_IsHidden", BIND_FLAGS);
        if (isHiddenField != null)
        {
            isHiddenField.SetValue(newItem, false);
        }

        // Other StatItem fields like m_ChangeBase, m_ChangeFormula
        // stay at default values (0, null, etc.), which matches GC2's default
        // behaviour when you add a stat entry via its editor.

        // 5) Add the new StatItem to the collection.
        if (list.IsFixedSize || innerListField.FieldType.IsArray)
        {
            // Underlying collection is an array (e.g. StatItem[])
            System.Array oldArray = rawCollection as System.Array;
            int oldCount = oldArray != null ? oldArray.Length : 0;

            // Determine array element type: usually StatItem
            System.Type elementType = innerListField.FieldType.IsArray
                ? innerListField.FieldType.GetElementType()
                : (oldArray != null ? oldArray.GetType().GetElementType() : statItemType);

            if (elementType == null)
            {
                // Fallback to statItemType just in case
                elementType = statItemType;
            }

            System.Array newArray = System.Array.CreateInstance(elementType, oldCount + 1);

            // Copy old elements if any
            if (oldArray != null && oldCount > 0)
            {
                System.Array.Copy(oldArray, newArray, oldCount);
            }
            // Append new item at the end
            newArray.SetValue(newItem, oldCount);

            // Assign the new array back to the field
            innerListField.SetValue(statListObj, newArray);
        }
        else
        {
            // Underlying collection is a resizable List<StatItem>
            list.Add(newItem);
            innerListField.SetValue(statListObj, list);
        }

        // Write StatList back to Class (not strictly necessary for reference types, but explicit)
        statsField.SetValue(cls, statListObj);

        return true;
    }

    /// <summary>
    /// Safely adds an Attribute to a GC2 Class using reflection and the internal
    /// AttributeList/AttributeItem types. Returns true if added, false otherwise.
    ///
    /// Also handles the case where the internal collection is a fixed-size array.
    /// </summary>
    private static bool AddAttributeToClassViaReflection(
        GC2Class cls,
        Attribute attributeAsset)
    {
        if (cls == null || attributeAsset == null) return false;

        var classType = typeof(GC2Class);

        // 1) Get private field "m_Attributes" from Class (type: AttributeList)
        var attributesField = classType.GetField("m_Attributes", BIND_FLAGS);
        if (attributesField == null)
        {
            Debug.LogError(
                "[ClassBulkTraitAssigner] Could not find field 'm_Attributes' on Class. " +
                "Game Creator internal layout may have changed.");
            return false;
        }

        object attributeListObj = attributesField.GetValue(cls);
        if (attributeListObj == null)
        {
            Debug.LogError(
                "[ClassBulkTraitAssigner] Class.m_Attributes is null. Cannot add Attribute.");
            return false;
        }

        // 2) Inside AttributeList there is another field "m_Attributes" which is the actual collection
        var attributeListType = attributeListObj.GetType();
        var innerListField = attributeListType.GetField("m_Attributes", BIND_FLAGS);
        if (innerListField == null)
        {
            Debug.LogError(
                "[ClassBulkTraitAssigner] Could not find inner field 'm_Attributes' on AttributeList. " +
                "Game Creator internal layout may have changed.");
            return false;
        }

        object rawCollection = innerListField.GetValue(attributeListObj);

        var list = rawCollection as System.Collections.IList;
        if (list == null)
        {
            Debug.LogError(
                "[ClassBulkTraitAssigner] AttributeList.m_Attributes is not an IList. " +
                "Game Creator internal layout may have changed.");
            return false;
        }

        // 3) Prevent duplicates: if Attribute already exists, bail out
        foreach (var entry in list)
        {
            if (entry == null) continue;

            var itemType = entry.GetType(); // AttributeItem type at runtime
            var attrFieldInfo = itemType.GetField("m_Attribute", BIND_FLAGS);
            if (attrFieldInfo == null) continue;

            var existing = attrFieldInfo.GetValue(entry) as Attribute;
            if (existing == attributeAsset)
            {
                return false; // Already present
            }
        }

        // 4) Create a new AttributeItem and initialize fields
        System.Type attributeItemType = typeof(GameCreator.Runtime.Stats.AttributeItem);
        object newItem = System.Activator.CreateInstance(attributeItemType);

        var newAttrField = attributeItemType.GetField("m_Attribute", BIND_FLAGS);
        if (newAttrField != null)
        {
            newAttrField.SetValue(newItem, attributeAsset);
        }

        // m_IsHidden field for attributes
        var isHiddenField = attributeItemType.GetField("m_IsHidden", BIND_FLAGS);
        if (isHiddenField != null)
        {
            isHiddenField.SetValue(newItem, false);
        }

        // m_ChangeStartPercent remains at default (GC2 behaviour on new entry)

        // 5) Add to collection (array or list)
        if (list.IsFixedSize || innerListField.FieldType.IsArray)
        {
            System.Array oldArray = rawCollection as System.Array;
            int oldCount = oldArray != null ? oldArray.Length : 0;

            System.Type elementType = innerListField.FieldType.IsArray
                ? innerListField.FieldType.GetElementType()
                : (oldArray != null ? oldArray.GetType().GetElementType() : attributeItemType);

            if (elementType == null)
                elementType = attributeItemType;

            System.Array newArray = System.Array.CreateInstance(elementType, oldCount + 1);

            if (oldArray != null && oldCount > 0)
            {
                System.Array.Copy(oldArray, newArray, oldCount);
            }

            newArray.SetValue(newItem, oldCount);

            innerListField.SetValue(attributeListObj, newArray);
        }
        else
        {
            list.Add(newItem);
            innerListField.SetValue(attributeListObj, list);
        }

        attributesField.SetValue(cls, attributeListObj);

        return true;
    }
}

#endif
