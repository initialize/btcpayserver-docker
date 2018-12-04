﻿using System;
using YamlDotNet;
using YamlDotNet.Helpers;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text;

namespace DockerFileBuildHelper
{
    class Program
    {
        static int Main(string[] args)
        {
            string outputFile = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-o")
                    outputFile = args[i + 1];
            }
            return new Program().Run(outputFile) ? 0 : 1;
        }

        private bool Run(string outputFile)
        {
            var fragmentDirectory = Path.GetFullPath(Path.Combine(FindRoot("contrib"), "..", "docker-compose-generator", "docker-fragments"));
            List<Task<bool>> downloading = new List<Task<bool>>();
            List<DockerInfo> dockerInfos = new List<DockerInfo>();
            foreach(var image in new[] 
            {
                Image.Parse("btcpayserver/docker-compose-generator"),
                Image.Parse("btcpayserver/docker-compose-builder:1.23.2"),
            }.Concat(GetImages(fragmentDirectory)))
            {
                Console.WriteLine($"Image: {image.ToString()}");
                var info = GetDockerInfo(image);
                if (info == null)
                {
                    Console.WriteLine($"Missing image info: {image}");
                    return false;
                }
                dockerInfos.Add(info);
                downloading.Add(CheckLink(info, info.DockerFilePath));
                downloading.Add(CheckLink(info, info.DockerFilePathARM32v7));
                downloading.Add(CheckLink(info, info.DockerFilePathARM64v8));
            }

            Task.WaitAll(downloading.ToArray());
            var canDownloadEverything = downloading.All(o => o.Result);
            if (!canDownloadEverything)
                return false;
            var builder = new StringBuilderEx();
            builder.AppendLine("#!/bin/bash");
            builder.AppendLine();
            builder.AppendLine("# This file is automatically generated by the DockerFileBuildHelper tool, run DockerFileBuildHelper/update-repo.sh to update it");
            builder.AppendLine("set -e");
            builder.AppendLine("DOCKERFILE=\"\"");
            builder.AppendLine();
            builder.AppendLine();
            foreach (var info in dockerInfos)
            {
                builder.AppendLine($"# Build {info.Image.Name}");
                bool mightBeUnavailable = false;
                if (info.DockerFilePath != null)
                {
                    var dockerFile = DockerFile.Parse(info.DockerFilePath);
                    builder.AppendLine($"# {info.GetGithubLinkOf(dockerFile.DockerFullPath)}");
                    builder.AppendLine($"DOCKERFILE=\"{dockerFile.DockerFullPath}\"");
                }
                else
                {
                    builder.AppendLine($"DOCKERFILE=\"\"");
                    mightBeUnavailable = true;
                }
                if (info.DockerFilePathARM32v7 != null)
                 {
                    var dockerFile = DockerFile.Parse(info.DockerFilePathARM32v7);
                    builder.AppendLine($"# {info.GetGithubLinkOf(dockerFile.DockerFullPath)}");
                    builder.AppendLine($"[[ \"$(uname -m)\" == \"armv7l\" ]] && DOCKERFILE=\"{dockerFile.DockerFullPath}\"");
                }
                if (info.DockerFilePathARM64v8 != null)
                {
                    var dockerFile = DockerFile.Parse(info.DockerFilePathARM64v8);
                    builder.AppendLine($"# {info.GetGithubLinkOf(dockerFile.DockerFullPath)}");
                    builder.AppendLine($"[[ \"$(uname -m)\" == \"aarch64\" ]] && DOCKERFILE=\"{dockerFile.DockerFullPath}\"");
                }
                if(mightBeUnavailable)
                {
                    builder.AppendLine($"if [[ \"$DOCKERFILE\" ]]; then");
                    builder.Indent++;
                }
                builder.AppendLine($"echo \"Building {info.Image.ToString()}\"");
                builder.AppendLine($"git clone {info.GitLink} {info.Image.Name}");
                builder.AppendLine($"cd {info.Image.Name}");
                builder.AppendLine($"git checkout {info.GitRef}");
                builder.AppendLine($"cd \"$(dirname $DOCKERFILE)\"");
                builder.AppendLine($"docker build -f \"$DOCKERFILE\" -t \"{info.Image}\" .");
                builder.AppendLine($"cd - && cd ..");
                if (mightBeUnavailable)
                {
                    builder.Indent--;
                    builder.AppendLine($"fi");
                }
                builder.AppendLine();
                builder.AppendLine();
            }
            var script = builder.ToString().Replace("\r\n", "\n");
            if (string.IsNullOrEmpty(outputFile))
                outputFile = "build-all.sh";
            File.WriteAllText(outputFile, script);
            Console.WriteLine($"Generated file \"{Path.GetFullPath(outputFile)}\"");
            return true;
        }
        HttpClient client = new HttpClient();
        private async Task<bool> CheckLink(DockerInfo info, string path)
        {
            if (path == null)
                return true;
            var link = info.GetGithubLinkOf(path);
            var resp = await client.GetAsync(link);
            if(!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"\tBroken link detected for image {info.Image} ({link})");
                return false;
            }
            return true;
        }

        private IEnumerable<Image> GetImages(string fragmentDirectory)
        {
            var deserializer = new DeserializerBuilder().Build();
            var serializer = new SerializerBuilder().Build();
            foreach (var file in Directory.EnumerateFiles(fragmentDirectory, "*.yml"))
            {
                var root = ParseDocument(file);
                if (root.TryGet("services") == null)
                    continue;
                foreach (var service in ((YamlMappingNode)root["services"]).Children)
                {
                    var imageStr = service.Value.TryGet("image");
                    if (imageStr == null)
                        continue;
                    var image = Image.Parse(imageStr.ToString());
                    yield return image;
                }
            }
        }
        private DockerInfo GetDockerInfo(Image image)
        {
            DockerInfo dockerInfo = new DockerInfo();
            switch (image.Name)
            {
                case "btglnd":
                    dockerInfo.DockerFilePath = "BTCPayServer.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/vutov/lnd";
                    dockerInfo.GitRef = "master";
                    break;
                case "docker-compose-builder":
                    dockerInfo.DockerFilePathARM32v7 = "linuxarm32v7.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/docker-compose-builder";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    break;
                case "docker-compose-generator":
                    dockerInfo.DockerFilePath = "docker-compose-generator/linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = "docker-compose-generator/linuxarm32v7.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/btcpayserver-docker";
                    dockerInfo.GitRef = $"dcg-latest";
                    break;
                case "docker-bitcoingold":
                    dockerInfo.DockerFilePath = $"bitcoingold/{image.Tag}/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/Vutov/docker-bitcoin";
                    dockerInfo.GitRef = "master";
                    break;
                case "clightning":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.GitLink = "https://github.com/NicolasDorier/lightning";
                    dockerInfo.GitRef = $"basedon-{image.Tag}";
                    break;
                case "lnd":
                    dockerInfo.DockerFilePath = "linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = "linuxarm32v7.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/lnd";
                    dockerInfo.GitRef = $"basedon-{image.Tag}";
                    break;
                case "bitcoin":
                    dockerInfo.DockerFilePath = $"Bitcoin/{image.Tag}/linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"Bitcoin/{image.Tag}/linuxarm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"Bitcoin/{image.Tag}/linuxarm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/dockerfile-deps";
                    dockerInfo.GitRef = $"Bitcoin/{image.Tag}";
                    break;
                case "dash":
                    dockerInfo.DockerFilePath = $"Dash/{image.Tag}/linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"Dash/{image.Tag}/linuxarm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"Dash/{image.Tag}/linuxarm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/dockerfile-deps";
                    dockerInfo.GitRef = $"Dash/{image.Tag}";
                    break;
                case "btcpayserver":
                    dockerInfo.DockerFilePath = "Dockerfile.linuxamd64";
                    dockerInfo.DockerFilePathARM32v7 = "Dockerfile.linuxarm32v7";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/btcpayserver";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    break;
                case "nbxplorer":
                    dockerInfo.DockerFilePath = "Dockerfile.linuxamd64";
                    dockerInfo.DockerFilePathARM32v7 = "Dockerfile.linuxarm32v7";
                    dockerInfo.GitLink = "https://github.com/dgarage/nbxplorer";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    break;
                case "dogecoin":
                    dockerInfo.DockerFilePath = $"dogecoin/{image.Tag}/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/rockstardev/docker-bitcoin";
                    dockerInfo.GitRef = "feature/dogecoin";
                    break;
                case "docker-bitcore":
                    dockerInfo.DockerFilePath = "btx-debian/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/dalijolijo/btcpayserver-docker-bitcore";
                    dockerInfo.GitRef = "master";
                    break;
                case "docker-feathercoin":
                    dockerInfo.DockerFilePath = $"feathercoin/{image.Tag}/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/ChekaZ/docker";
                    dockerInfo.GitRef = "master";
                    break;
                case "docker-groestlcoin":
                    dockerInfo.DockerFilePath = $"groestlcoin/{image.Tag}/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/NicolasDorier/docker-bitcoin";
                    dockerInfo.GitRef = "master";
                    break;
                case "docker-viacoin":
                    dockerInfo.DockerFilePath = $"viacoin/{image.Tag}/docker-viacoin";
                    dockerInfo.GitLink = "https://github.com/viacoin/docker-viacoin";
                    dockerInfo.GitRef = "master";
                    break;
                case "docker-litecoin":
                    dockerInfo.DockerFilePath = $"litecoin/{image.Tag}/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/NicolasDorier/docker-bitcoin";
                    dockerInfo.GitRef = "master";
                    break;
                case "docker-monacoin":
                    dockerInfo.DockerFilePath = $"monacoin/{image.Tag}/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/wakiyamap/docker-bitcoin";
                    dockerInfo.GitRef = "master";
                    break;
                case "nginx":
                    dockerInfo.DockerFilePath = $"stable/stretch/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/nginxinc/docker-nginx";
                    dockerInfo.GitRef = $"master";
                    break;
                case "docker-gen":
                    dockerInfo.DockerFilePath = $"linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"linuxarm32v7.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/docker-gen";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    break;
                case "letsencrypt-nginx-proxy-companion":
                    dockerInfo.DockerFilePath = $"linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"linuxarm32v7.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/docker-letsencrypt-nginx-proxy-companion";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    break;
                case "postgres":
                    dockerInfo.DockerFilePath = $"9.6/Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"9.6/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/docker-library/postgres";
                    dockerInfo.GitRef = $"b7cb3c6eacea93be2259381033be3cc435649369";
                    break;
                case "traefik":
                    dockerInfo.DockerFilePath = $"scratch/amd64/Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"scratch/arm/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/containous/traefik-library-image";
                    dockerInfo.GitRef = $"master";
                    break;
                default:
                    return null;
            }
            dockerInfo.DockerHubLink = image.DockerHubLink;
            dockerInfo.Image = image;
            return dockerInfo;
        }

        private YamlMappingNode ParseDocument(string fragment)
        {
            var input = new StringReader(File.ReadAllText(fragment));
            YamlStream stream = new YamlStream();
            stream.Load(input);
            return (YamlMappingNode)stream.Documents[0].RootNode;
        }

        private static void DeleteDirectory(string outputDirectory)
        {
            try
            {
                Directory.Delete(outputDirectory, true);
            }
            catch
            {
            }
        }

        private static string FindRoot(string rootDirectory)
        {
            string directory = Directory.GetCurrentDirectory();
            int i = 0;
            while (true)
            {
                if (i > 10)
                    throw new DirectoryNotFoundException(rootDirectory);
                if (directory.EndsWith(rootDirectory))
                    return directory;
                directory = Path.GetFullPath(Path.Combine(directory, ".."));
                i++;
            }
        }
    }
}
