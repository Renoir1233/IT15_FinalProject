using System.Globalization;
using FinalProject.Data;
using FinalProject.Models;
using FinalProject.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString, sqlOptions =>
        sqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(5),
            errorNumbersToAdd: null)));

builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredLength = 8;
        
        // Configure 2FA tokens
        options.Tokens.AuthenticatorTokenProvider = "CompanyAuthenticator";
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddTokenProvider<CompanyAuthenticatorTokenProvider>("CompanyAuthenticator");

// Configure Authorization Policies
builder.Services.AddAuthorization(options =>
{
    // Super Admin only policies
    options.AddPolicy("SuperAdminOnly", policy => 
        policy.RequireRole(RoleConstants.SuperAdmin));
    
    options.AddPolicy("ManageSystemSettings", policy => 
        policy.RequireRole(RoleConstants.SuperAdmin));
    
    options.AddPolicy("ApproveAdminAccounts", policy => 
        policy.RequireRole(RoleConstants.SuperAdmin));

    // Admin, CompanyOwner, and Super Admin policies
    options.AddPolicy("AdminOrSuperAdmin", policy => 
        policy.RequireRole(RoleConstants.Admin, RoleConstants.SuperAdmin));
    
    options.AddPolicy("AdminCompanyOwnerOrSuperAdmin", policy => 
        policy.RequireRole(RoleConstants.Admin, RoleConstants.CompanyOwner, RoleConstants.SuperAdmin));
    
    options.AddPolicy("ManageUsers", policy => 
        policy.RequireRole(RoleConstants.Admin, RoleConstants.SuperAdmin));
    
    options.AddPolicy("ManageCampaigns", policy => 
        policy.RequireRole(RoleConstants.MarketingStaff, RoleConstants.Admin, RoleConstants.CompanyOwner, RoleConstants.SuperAdmin));
    
    options.AddPolicy("ViewAnalytics", policy => 
        policy.RequireRole(RoleConstants.Admin, RoleConstants.CompanyOwner, RoleConstants.SuperAdmin));

    // Marketing Staff and above policies
    options.AddPolicy("CreateCampaigns", policy => 
        policy.RequireRole(RoleConstants.MarketingStaff, RoleConstants.Admin, RoleConstants.CompanyOwner, RoleConstants.SuperAdmin));
    
    options.AddPolicy("EditCampaigns", policy => 
        policy.RequireRole(RoleConstants.MarketingStaff, RoleConstants.Admin, RoleConstants.CompanyOwner, RoleConstants.SuperAdmin));
    
    options.AddPolicy("ViewCampaignResults", policy => 
        policy.RequireRole(RoleConstants.MarketingStaff, RoleConstants.Admin, RoleConstants.CompanyOwner, RoleConstants.SuperAdmin));

    // All authenticated users
    options.AddPolicy("AuthenticatedUser", policy => 
        policy.RequireAuthenticatedUser());
});

builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("EmailSettings"));
builder.Services.AddTransient<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<IUserAuthorizationService, UserAuthorizationService>();
builder.Services.AddSingleton<EmailTemplateService>();
builder.Services.AddSingleton<LoyaltyEmailTemplateService>();
builder.Services.AddSingleton<InvoiceEmailTemplateService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<FinalProject.Services.PlanEnforcementFilter>();

// TOTP / 2FA Services
builder.Services.AddScoped<TotpService>();

// reCAPTCHA
builder.Services.Configure<ReCaptchaSettings>(builder.Configuration.GetSection("ReCaptcha"));
builder.Services.AddHttpClient<ReCaptchaService>();

// PayMongo
builder.Services.Configure<PayMongoSettings>(builder.Configuration.GetSection("PayMongo"));
builder.Services.AddHttpClient<PayMongoService>();

// SMS & Workflow Automation
builder.Services.AddScoped<WorkflowService>();
builder.Services.AddHostedService<WorkflowBackgroundService>();
builder.Services.AddHostedService<CampaignArchiveService>();

// Add services to the container.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<FinalProject.Services.PlanEnforcementFilter>();
});
builder.Services.AddRazorPages();

var app = builder.Build();

var philippineCulture = new CultureInfo("en-PH");
CultureInfo.DefaultThreadCurrentCulture = philippineCulture;
CultureInfo.DefaultThreadCurrentUICulture = philippineCulture;

app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(philippineCulture),
    SupportedCultures = [philippineCulture],
    SupportedUICultures = [philippineCulture]
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapRazorPages();

try
{
    // Auto-apply pending migrations on startup
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        
        try
        {
            db.Database.Migrate();
            logger.LogInformation("Database migrations applied successfully");
        }
        catch (Exception migEx)
        {
            logger.LogError(migEx, "Migration failed: {Message}", migEx.Message);
            // Try to fix the LoyaltyAccounts index manually if migration failed
            try
            {
                db.Database.ExecuteSqlRaw(@"
                    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_LoyaltyAccounts_Email' AND object_id = OBJECT_ID('LoyaltyAccounts'))
                    BEGIN
                        DROP INDEX [IX_LoyaltyAccounts_Email] ON [LoyaltyAccounts];
                    END
                ");
                db.Database.ExecuteSqlRaw(@"
                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_LoyaltyAccounts_Email_CompanyId' AND object_id = OBJECT_ID('LoyaltyAccounts'))
                    BEGIN
                        CREATE UNIQUE INDEX [IX_LoyaltyAccounts_Email_CompanyId] ON [LoyaltyAccounts] ([Email], [CompanyId]) WHERE [CompanyId] IS NOT NULL;
                    END
                ");
                logger.LogInformation("LoyaltyAccounts index fixed manually");
            }
            catch (Exception sqlEx)
            {
                logger.LogWarning(sqlEx, "Manual index fix also failed, continuing: {Message}", sqlEx.Message);
            }
        }
    }

    await SeedData.EnsureSeedDataAsync(app.Services);
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    logger.LogError(ex, "Seed data failed during startup: {Message}", ex.Message);
}

app.Run();
