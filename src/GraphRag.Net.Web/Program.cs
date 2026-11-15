using AntDesign.ProLayout;
using GraphRag.Net;
using GraphRag.Net.Options;
using GraphRag.Net.Web.Mock;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Pi.Ai.Types;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddAntDesign();
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(sp.GetService<NavigationManager>()!.BaseUri)
});
builder.Services.Configure<ProSettings>(builder.Configuration.GetSection("ProSettings"));

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => {
    c.SwaggerDoc("v1", new() { Title = "GraphRag.Net.Api", Version = "v1" });
    //添加Api层注释（true表示显示控制器注释）
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    c.IncludeXmlComments(xmlPath, true);
});

builder.Configuration.GetSection("GraphOpenAI").Get<GraphOpenAIOption>();
builder.Configuration.GetSection("TextChunker").Get<TextChunkerOption>();
builder.Configuration.GetSection("GraphSearch").Get<GraphSearchOption>();
builder.Configuration.GetSection("PiAgent").Get<PiAgentOption>();
//builder.Configuration.GetSection("GraphDBConnection").Get<GraphDBConnectionOption>();

// pi.net AI 网关：默认按 GraphOpenAI 配置装配 OpenAiCompletions provider。
builder.Services.AddGraphRagNet();

// 自定义模型 / 离线调试：注入自定义 StreamFn（pi.net 的唯一模型出口）。
// 例如使用仓库自带的 Faux 提供方，无需任何密钥即可跑通 agent loop：
//   StreamFn mockStream = Faux.CreateStreamFn([Faux.Response.Text("mock answer")]);
//   var custom = new Kernel(mockStream);
//   builder.Services.AddGraphRagNet(custom);
// 也可参考 Mock/MockChatCompletion.cs 中的 MockPiStream。

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore/swashbuckle.
    app.UseHsts();
}

app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.UseAuthorization();

app.MapControllers();

app.Run();
