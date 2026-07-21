using IndieFi.Web.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db)
    {
        _db = db;
    }

    public bool DatabaseConnected { get; private set; }

    public string? DatabaseError { get; private set; }

    public async Task OnGetAsync()
    {
        try
        {
            DatabaseConnected = await _db.Database.CanConnectAsync();
        }
        catch (Exception exception)
        {
            DatabaseConnected = false;
            DatabaseError = exception.Message;
        }
    }
}
