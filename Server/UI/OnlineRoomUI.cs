using System;
using UnityEngine;
using Cute;
using Wizard;

namespace Shadowbus.Server.UI
{
    /// <summary>
    /// 在线房间 UI（在主菜单显示）
    /// </summary>
    public class OnlineRoomUI : MonoBehaviour
    {
        private Rect _windowRect = new Rect(Screen.width - 420, 20, 400, 300);
        private string _roomCodeInput = "";
        private bool _showWindow = false;

        private void Update()
        {
            // 按 F9 切换窗口显示
            if (Input.GetKeyDown(KeyCode.F9))
            {
                _showWindow = !_showWindow;
                Plugin.Logger.LogInfo($"[OnlineRoomUI] Window toggled: {_showWindow}");
            }
        }

        private void OnGUI()
        {
            if (!_showWindow)
                return;

            _windowRect = GUI.Window(54321, _windowRect, DrawWindow, "在线对战房间");
        }

        private void DrawWindow(int windowId)
        {
            GUILayout.BeginVertical();

            GUILayout.Label("=== 服务器状态 ===", GUI.skin.box);

            bool serverRunning = OnlineRuntime.IsServerRunning;
            GUILayout.Label($"状态: {(serverRunning ? "运行中" : "未运行")}");

            if (serverRunning)
            {
                GUILayout.Label($"房间码: {OnlineRuntime.RoomCode}");

                if (GUILayout.Button("复制房间码"))
                {
                    GUIUtility.systemCopyBuffer = OnlineRuntime.RoomCode;
                    Plugin.Logger.LogInfo($"[OnlineRoomUI] Room code copied: {OnlineRuntime.RoomCode}");
                }

                if (GUILayout.Button("停止服务器"))
                {
                    OnlineRuntime.StopServer();
                }
            }
            else
            {
                if (GUILayout.Button("创建房间", GUILayout.Height(40)))
                {
                    CreateRoom();
                }
            }

            GUILayout.Space(10);
            GUILayout.Label("=== 加入房间 ===", GUI.skin.box);

            GUILayout.Label("房间码:");
            _roomCodeInput = GUILayout.TextField(_roomCodeInput);

            if (GUILayout.Button("从剪贴板粘贴"))
            {
                _roomCodeInput = GUIUtility.systemCopyBuffer;
            }

            GUI.enabled = !string.IsNullOrEmpty(_roomCodeInput);
            if (GUILayout.Button("加入房间", GUILayout.Height(40)))
            {
                JoinRoom(_roomCodeInput);
            }
            GUI.enabled = true;

            GUILayout.Space(10);
            GUILayout.Label("提示: 按 F9 打开/关闭此窗口", GUI.skin.box);

            GUILayout.EndVertical();

            GUI.DragWindow();
        }

        private void CreateRoom()
        {
            try
            {
                if (OnlineRuntime.StartServer())
                {
                    Plugin.Logger.LogInfo($"[OnlineRoomUI] Room created: {OnlineRuntime.RoomCode}");

                    // 显示成功对话框
                    ShowDialog("房间创建成功",
                        $"房间码: {OnlineRuntime.RoomCode}\n\n请将此房间码发送给其他玩家。\n\n房间码已自动复制到剪贴板。");

                    GUIUtility.systemCopyBuffer = OnlineRuntime.RoomCode;
                }
                else
                {
                    ShowDialog("创建失败", "无法创建房间，请查看日志了解详情。");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[OnlineRoomUI] Create room error: {ex}");
                ShowDialog("创建失败", $"创建房间时出错: {ex.Message}");
            }
        }

        private void JoinRoom(string roomCode)
        {
            try
            {
                Plugin.Logger.LogInfo($"[OnlineRoomUI] Attempting to join room: {roomCode}");

                if (OnlineRuntime.ConnectToServer(roomCode))
                {
                    Plugin.Logger.LogInfo("[OnlineRoomUI] Successfully connected to room");
                    ShowDialog("加入成功", "已成功连接到房间！");
                }
                else
                {
                    ShowDialog("加入失败", "无法连接到房间，请检查房间码是否正确。");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[OnlineRoomUI] Join room error: {ex}");
                ShowDialog("加入失败", $"加入房间时出错: {ex.Message}");
            }
        }

        private void ShowDialog(string title, string message)
        {
            try
            {
                UIManager uiManager = UIManager.GetInstance();
                if (uiManager != null)
                {
                    DialogBase dialog = uiManager.CreateDialogClose(false, false);
                    dialog.SetSize(DialogBase.Size.M);
                    dialog.SetTitleLabel($"{title}\n\n{message}");
                    dialog.SetButtonLayout(DialogBase.ButtonLayout.CloseBtn);
                }
                else
                {
                    Plugin.Logger.LogWarning($"[OnlineRoomUI] UIManager not available. Message: {title} - {message}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[OnlineRoomUI] ShowDialog error: {ex}");
            }
        }
    }
}
