using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;
using tusdotnet;
using tusdotnet.Helpers;
using tusdotnet.Interfaces;
using tusdotnet.Models;
using tusdotnet.Models.Concatenation;
using tusdotnet.Models.Configuration;
using tusdotnet.Stores;

namespace LargeFileUpload {
    public class Startup {
        private readonly IWebHostEnvironment _environment;

        public Startup(IConfiguration configuration, IWebHostEnvironment environment) {
            Configuration = configuration;
            _environment = environment;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services) {
            services.AddRazorPages();
            services.AddCors();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env) {
            app.Use((context, next) => {
                // Default limit was changed some time ago. Should work by setting MaxRequestBodySize to null using ConfigureKestrel but this does not seem to work for IISExpress.
                // Source: https://github.com/aspnet/Announcements/issues/267
                context.Features.Get<IHttpMaxRequestBodySizeFeature>()
                    .MaxRequestBodySize = null;
                return next.Invoke();
            });
            if (env.IsDevelopment()) {
                app.UseDeveloperExceptionPage();
            }
            else {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseCors(builder => builder
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowAnyOrigin()
                .WithExposedHeaders(CorsHelper.GetExposedHeaders()
                ));

            app.UseTus(httpContext => new DefaultTusConfiguration {
                Store = new TusDiskStore(@$"{_environment.WebRootPath}/tusfiles/"),
                UrlPath = "/files",
                Events = new Events {
                    // OnFileCompleteAsync = async eventContext => {
                    //     // eventContext.FileId is the id of the file that was uploaded.
                    //     // eventContext.Store is the data store that was used (in this case an instance of the TusDiskStore)
                    //
                    //     // A normal use case here would be to read the file and do some processing on it.
                    //     ITusFile file = await eventContext.GetFileAsync();
                    //     var result = await DoSomeProcessing(file, eventContext.CancellationToken)
                    //         .ConfigureAwait(false);
                    //
                    //     if (!result) {
                    //         //throw new MyProcessingException("Something went wrong during processing");
                    //     }
                    // },
                    OnBeforeCreateAsync = async ctx => {
                        // Partial files are not complete so we do not need to validate
                        // the metadata in our example.
                        if (ctx.FileConcatenation is FileConcatPartial) {
                            return;
                        }

                        if (!ctx.Metadata.ContainsKey("filename") || ctx.Metadata["filename"]
                                .HasEmptyValue) {
                            ctx.FailRequest("name metadata must be specified. ");
                        }



                        var metaData = ctx.Metadata;

                        metaData.TryGetValue("filename", out var fileName);
                        var name = Encoding.UTF8.GetString(fileName!.GetBytes());


                        if (ctx.HttpContext.User.Identity is {IsAuthenticated: true}) {
                            var claims = ctx.HttpContext.User.Claims.ToList();
                            var userId = Guid.Parse(claims.FirstOrDefault(predicate: c =>
                                    c.Type ==
                                    "id")
                                ?.Value!);
                        }

                        return;
                    },
                    OnFileCompleteAsync = async ctx =>
                    {
                        var file = await ctx.GetFileAsync();
                        var metaData = await file.GetMetadataAsync(new CancellationToken());


                        metaData.TryGetValue("filename", out var fileName);
                        var name =  Encoding.UTF8.GetString(fileName!.GetBytes());

                        metaData.TryGetValue("postid", out var postId);
                        var post =  Guid.Parse(Encoding.UTF8.GetString(postId!.GetBytes()));

                        var path=  await HandleAfterUpload.SaveFileInPostFolder(Environment.WebRootPath, file.Id, name,
                            post);
                        Console.WriteLine("");
                        logger.LogInformation($"Upload of {ctx.FileId} completed using {ctx.Store.GetType().FullName} additional info is");
                        // If the store implements ITusReadableStore one could access the completed file here.
                        // The default TusDiskStore implements this interface:
                        //var file = await ctx.GetFileAsync();

                        return;
                    },
                }
            });

            app.UseRouting();
            app.UseAuthorization();
            app.UseEndpoints(endpoints => { endpoints.MapRazorPages(); });
        }

        private Task<bool> DoSomeProcessing(ITusFile file, CancellationToken cancellationToken) {
            return Task.FromResult(true);
        }
    }

    public static class HandleAfterUpload {
        public static async Task<bool> CheckDirectoryName(Guid postId) { return false; }
        public static async Task<string> SaveFileInPostFolder(string webRootPath, string fileId,
            string fileName, Guid postId)
        {
            var post = GetPostById(postId);
            int i = 1;
            while (File.Exists(@$"{post.StorageDrive.Path}/CompletedFiles/{post!.Id}/{fileName}"))
            {
                var n = fileName.Split('.');
                n[n.Length-2] += "-"+i;
                var newName = "";
                foreach (var s in n)
                {
                    newName += "."+s;
                }
                if (!File.Exists(@$"{post.StorageDrive.Path}/CompletedFiles/{post.Id}/{newName}"))
                {
                    fileName = newName;
                }
                i++;
            }
            File.Move(@$"{webRootPath}/tempFiles/tusFiles/{fileId}", @$"{post.StorageDrive.Path}/CompletedFiles/{post.Id}/{fileName}");
            FileInfo fi = new FileInfo(@$"{post.StorageDrive.Path}/CompletedFiles/{post.Id}/{fileName}");

            var postFile = new PostFiles()
            {
                DownloadsCount = 0,
                FileName = fileName,
                FileUrl = @$"/CompletedFiles/{post.Id}/{fileName}",
                PostId = postId,
                UploadDate = DateTime.Now,
                Size = fi.Length
            };

            await dbContext.PostFiles.AddAsync(postFile);
            await dbContext.SaveChangesAsync();
            return @$"/CompletedFiles/{post.Id}/{fileName}";
        }
        public static bool CheckPostOwner(Guid postId, Guid userId) { return false; }
    }
}