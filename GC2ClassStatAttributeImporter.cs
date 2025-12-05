//using System.Collections.Generic;
//using System.IO;
//using UnityEditor;
//using UnityEngine;
//using GameCreator.Runtime.Stats;
//using GameCreator.Runtime.Common;

//public class GC2ClassStatAttributeImporter : EditorWindow
//{
//    Serialized fields for user input
//    private Class selectedClass; // The Class ScriptableObject to modify
//    private string selectedFolder = "Assets"; // The root folder to scan for Stats and Attributes

//    private List<Stat> foundStats = new List<Stat>(); // Collected Stats
//    private List<GameCreator.Runtime.Stats.Attribute> foundAttributes = new List<GameCreator.Runtime.Stats.Attribute>(); // Collected Attributes

//    [MenuItem("Tools/Game Creator/Stats/Import Stats & Attributes to Class")]
//    public static void ShowWindow()
//    {
//        GetWindow<GC2ClassStatAttributeImporter>("Import Stats & Attributes");
//    }

//    private void OnGUI()
//    {
//        GUILayout.Label("GC2 Class Stat & Attribute Importer", EditorStyles.boldLabel);
//        EditorGUILayout.Space();

//        Class Selection
//        selectedClass = (Class)EditorGUILayout.ObjectField("Select Class", selectedClass, typeof(Class), false);

//        Folder Selection
//        EditorGUILayout.LabelField("Select Folder to Scan:");
//        EditorGUILayout.BeginHorizontal();
//        selectedFolder = EditorGUILayout.TextField(selectedFolder);
//        if (GUILayout.Button("Browse", GUILayout.Width(80)))
//        {
//            string folderPath = EditorUtility.OpenFolderPanel("Select Folder", "Assets", "");
//            if (!string.IsNullOrEmpty(folderPath))
//            {
//                selectedFolder = "Assets" + folderPath.Replace(Application.dataPath, "");
//            }
//        }
//        EditorGUILayout.EndHorizontal();

//        EditorGUILayout.Space();

//        Scan and Import Buttons
//        if (GUILayout.Button("Scan Folder for Stats & Attributes"))
//        {
//            ScanForStatsAndAttributes();
//        }

//        if (GUILayout.Button("Import to Class"))
//        {
//            ImportToClass();
//        }
//    }

//    private void ScanForStatsAndAttributes()
//    {
//        foundStats.Clear();
//        foundAttributes.Clear();

//        Ensure a valid folder is selected
//        if (string.IsNullOrEmpty(selectedFolder))
//        {
//            Debug.LogError("No folder selected for scanning.");
//            return;
//        }

//        Find all ScriptableObjects in the selected folder and subfolders
//        string[] assetPaths = AssetDatabase.FindAssets("t:ScriptableObject", new[] { selectedFolder });

//        foreach (string assetGUID in assetPaths)
//        {
//            string assetPath = AssetDatabase.GUIDToAssetPath(assetGUID);
//            ScriptableObject asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);

//            if (asset is Stat stat)  // If the asset is a Stat, add it to foundStats
//            {
//                foundStats.Add(stat);
//            }
//            else if (asset is GameCreator.Runtime.Stats.Attribute attribute)  // If the asset is an Attribute, add it to foundAttributes
//            {
//                foundAttributes.Add(attribute);
//            }
//        }

//        Debug.Log($"Scan complete. Found {foundAttributes.Count} Attributes and {foundStats.Count} Stats.");
//    }
//    private void ImportToClass()
//    {
//        if (selectedClass == null)
//        {
//            Debug.LogError("❌ No Class ScriptableObject selected.");
//            return;
//        }

//        Debug.Log($"[DEBUG] Importing Stats & Attributes into Class: {selectedClass.name}");

//        Ensure Undo support for tracking changes

//       Undo.RecordObject(selectedClass, "Import Stats & Attributes");

//        Load SerializedObject for the Class

//       SerializedObject classSO = new SerializedObject(selectedClass);

//            Debug all available serialized properties in the Class

//       Debug.Log("[DEBUG] Checking Serialized Properties for Class...");
//            DebugSerializedProperties(classSO);

//            Attempt to locate the correct SerializedProperty names dynamically

//       SerializedProperty attributesProp = classSO.FindProperty("m_Attributes.m_Attributes"); // Default guess
//            SerializedProperty statsProp = classSO.FindProperty("m_Stats.m_Stats"); // Default guess

//        if (attributesProp == null)
//                {
//                    attributesProp = classSO.FindProperty("m_Attributes.m_Items"); // Try alternative structure
//                }
//        if (statsProp == null)
//        {
//            statsProp = classSO.FindProperty("m_Stats.m_Items"); // Try alternative structure
//        }

//        Verify that we found the correct serialized properties
//        if (attributesProp == null)
//        {
//            Debug.LogError("❌ Could not find 'm_Attributes' property in Class. Ensure correct field name.");
//            return;
//        }
//        Debug.Log($"✅ Found 'm_Attributes' property. Existing count: {attributesProp.arraySize}");

//        if (statsProp == null)
//        {
//            Debug.LogError("❌ Could not find 'm_Stats' property in Class. Ensure correct field name.");
//            return;
//        }
//        Debug.Log($"✅ Found 'm_Stats' property. Existing count: {statsProp.arraySize}");

//        **Step 1: Clear Existing Entries**
//        Debug.Log("[DEBUG] Clearing existing stats & attributes...");
//        attributesProp.ClearArray();
//        statsProp.ClearArray();

//        **Step 2: Add Attributes**
//        for (int i = 0; i < foundAttributes.Count; i++)
//        {
//            attributesProp.InsertArrayElementAtIndex(i);
//            SerializedProperty element = attributesProp.GetArrayElementAtIndex(i);
//            SerializedProperty attributeRef = element.FindPropertyRelative("m_Attribute");

//            if (attributeRef != null)
//            {
//                attributeRef.objectReferenceValue = foundAttributes[i];
//                Debug.Log($"✅ Added Attribute: {foundAttributes[i].name}");
//            }
//            else
//            {
//                Debug.LogError($"❌ Could not find 'm_Attribute' property at index {i} inside AttributeList.");
//            }
//        }

//        **Step 3: Add Stats**
//        for (int i = 0; i < foundStats.Count; i++)
//        {
//            statsProp.InsertArrayElementAtIndex(i);
//            SerializedProperty element = statsProp.GetArrayElementAtIndex(i);
//            SerializedProperty statRef = element.FindPropertyRelative("m_Stat");

//            if (statRef != null)
//            {
//                statRef.objectReferenceValue = foundStats[i];
//                Debug.Log($"✅ Added Stat: {foundStats[i].name}");
//            }
//            else
//            {
//                Debug.LogError($"❌ Could not find 'm_Stat' property at index {i} inside StatList.");
//            }
//        }

//        **Step 4: Apply Changes and Refresh UI**
//        classSO.ApplyModifiedProperties();
//        EditorUtility.SetDirty(selectedClass);
//        AssetDatabase.SaveAssets();
//        AssetDatabase.Refresh();

//        Debug.Log($"🎉 Successfully imported {foundAttributes.Count} Attributes and {foundStats.Count} Stats into {selectedClass.name}.");
//    }

//    / <summary>
//    / Logs all available serialized properties of the given SerializedObject.
//    / This helps identify the correct property names when debugging.
//    / </summary>
//    / <param name = "serializedObject" > The SerializedObject to inspect.</param>
//    private void DebugSerializedProperties(SerializedObject serializedObject)
//    {
//        SerializedProperty property = serializedObject.GetIterator();
//        Debug.Log("🔍 [DEBUG] Listing all Serialized Properties:");

//        while (property.NextVisible(true))
//        {
//            Debug.Log($"➡ Property: {property.name} (Type: {property.propertyType})");
//        }
//    }
//}