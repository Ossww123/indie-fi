using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace IndieFi.Web.Modules.Notifications.Application;

[Authorize]
public sealed class NotificationHub : Hub
{
}
