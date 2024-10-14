using Nito.AsyncEx;
//To allow async event though GLFW liked being used in a single thread 
AsyncContext.Run(async () =>
{
    await HelloTriangle.Run();
});
