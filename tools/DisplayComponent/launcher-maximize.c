void util_toggle_maximize()
{
    static HWND saved_window;
    static RECT restore_client;
    static RECT maximized_client;
    static BOOL maximized;

    if (!g_config.resizable || !g_config.windowed || g_config.fullscreen || !g_ddraw.width)
        return;

    RECT current;
    POINT origin = { 0, 0 };
    if (!real_GetClientRect(g_ddraw.hwnd, &current) || !real_ClientToScreen(g_ddraw.hwnd, &origin))
        return;
    OffsetRect(&current, origin.x, origin.y);

    if (saved_window != g_ddraw.hwnd || !EqualRect(&current, &maximized_client))
        maximized = FALSE;

    if (maximized)
    {
        maximized = FALSE;
        util_set_window_rect(restore_client.left, restore_client.top,
            restore_client.right - restore_client.left, restore_client.bottom - restore_client.top, 0);
        return;
    }

    MONITORINFO monitor = { sizeof(MONITORINFO) };
    if (!GetMonitorInfoA(MonitorFromWindow(g_ddraw.hwnd, MONITOR_DEFAULTTONEAREST), &monitor))
        return;

    RECT target = monitor.rcWork;
    if (!util_unadjust_window_rect(&target, real_GetWindowLongA(g_ddraw.hwnd, GWL_STYLE),
        GetMenu(g_ddraw.hwnd) != NULL, real_GetWindowLongA(g_ddraw.hwnd, GWL_EXSTYLE)))
        return;
    if (target.right <= target.left || target.bottom <= target.top)
        return;

    saved_window = g_ddraw.hwnd;
    restore_client = current;
    maximized_client = target;
    maximized = TRUE;
    util_set_window_rect(target.left, target.top, target.right - target.left, target.bottom - target.top, 0);
}
