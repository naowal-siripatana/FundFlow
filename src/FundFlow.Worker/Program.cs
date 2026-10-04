using FundFlow.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<OrderConsumer>();
builder.Build().Run();
