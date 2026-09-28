using UnityEngine;
using UnityEditor;
using System.Net;
using System.Text;
using System.Threading;

[InitializeOnLoad]
public static class AgentLiveBridge
{
    private static HttpListener listener;
    private static Thread serverThread;
    private static bool isRunning = false;

    static AgentLiveBridge()
    {
        StartServer();
        EditorApplication.quitting += StopServer;
    }

    private static void StartServer()
    {
        if (isRunning) return;
        listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:8088/");
        listener.Start();
        isRunning = true;
        
        serverThread = new Thread(ListenLoop) { IsBackground = true };
        serverThread.Start();
        Debug.Log("<color=cyan>[Agent Bridge] AI Link Active on http://127.0.0.1:8088/</color>");
    }

    private static void ListenLoop()
    {
        while (isRunning && listener != null && listener.IsListening)
        {
            try
            {
                var context = listener.GetContext();
                string route = context.Request.Url.AbsolutePath;
                
                // Route the command to the main Unity thread
                EditorApplication.delayCall += () => RouteCommand(route);
                
                byte[] response = Encoding.UTF8.GetBytes("Command Received: " + route);
                context.Response.OutputStream.Write(response, 0, response.Length);
                context.Response.Close();
            }
            catch { }
        }
    }

    private static void RouteCommand(string route)
    {
        if (route == "/build_scene")
        {
            EditorApplication.ExecuteMenuItem("PathSense/Automate Mine Scene");
        }
    }

    private static void StopServer()
    {
        isRunning = false;
        if (listener != null) { listener.Stop(); listener.Close(); }
    }
}