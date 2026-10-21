using Microsoft.AspNetCore.Mvc;

namespace HiveArena.Api.Controllers;

public sealed class HomeController : Controller
{
    public IActionResult Index() => View();
}
