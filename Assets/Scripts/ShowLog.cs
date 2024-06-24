using UnityEngine;
using System.Collections;

public class ShowLog : MonoBehaviour
{
    uint qsize = 20;  // number of messages to keep
    Queue myLogQueue = new Queue();
    GUIStyle logStyle;

    void Start()
    {
        Debug.Log("Started up logging.");
        logStyle = new GUIStyle();
        logStyle.fontSize = 14;
        logStyle.normal.textColor = Color.white;
    }

    void OnEnable()
    {
        Application.logMessageReceived += HandleLog;
    }

    void OnDisable()
    {
        Application.logMessageReceived -= HandleLog;
    }

    void HandleLog(string logString, string stackTrace, LogType type)
    {
        myLogQueue.Enqueue("[" + type + "] : " + logString);
        if (type == LogType.Exception)
            myLogQueue.Enqueue(stackTrace);
        while (myLogQueue.Count > qsize)
            myLogQueue.Dequeue();
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(0, 0, 800, Screen.height));
        GUILayout.Label("\n" + string.Join("\n", myLogQueue.ToArray()), logStyle);
        GUILayout.EndArea();
    }
}
