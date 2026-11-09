using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AntDeploy.Core.Util
{
    /// <summary>
    /// Docker 镜像引用解析（从 AntDeployWinform.Util.ImageReference 移植）。
    /// jib 用 ImageReference.Parse(targetImage)==null 判断走本地 tar 打包还是推送远程仓库。
    /// </summary>
    public sealed class ImageReference
    {
        private const string DOCKER_HUB_REGISTRY = "registry-1.docker.io";
        private const string DEFAULT_TAG = "latest";
        private const string LIBRARY_REPOSITORY_PREFIX = "library/";

        private const string REGISTRY_COMPONENT_REGEX =
            "(?:[a-zA-Z\\d]|(?:[a-zA-Z\\d][a-zA-Z\\d-]*[a-zA-Z\\d]))";

        private static readonly string REGISTRY_REGEX =
            $"{REGISTRY_COMPONENT_REGEX}(?:\\.{REGISTRY_COMPONENT_REGEX})*(?::\\d+)?";

        private const string REPOSITORY_COMPONENT_REGEX = "[a-z\\d]+(?:(?:[_.]|__|-+)[a-z\\d]+)*";

        private static readonly string REPOSITORY_REGEX =
            $"(?:{REPOSITORY_COMPONENT_REGEX}/)*{REPOSITORY_COMPONENT_REGEX}";

        private const string TAG_REGEX = "[\\w][\\w.-]{0,127}";

        private static readonly string REFERENCE_REGEX =
            $"^(?:({REGISTRY_REGEX})/)?({REPOSITORY_REGEX})(?:(?::({TAG_REGEX}))|(?:@({DescriptorDigest.DigestRegex})))?$";

        private static readonly Regex REFERENCE_PATTERN = new Regex(REFERENCE_REGEX);

        public static ImageReference Parse(string reference)
        {
            try
            {
                Match matcher = REFERENCE_PATTERN.Match(reference);

                if (!matcher.Success || matcher.Groups.Count < 4)
                {
                    throw new Exception(reference);
                }

                string registry = matcher.Groups[1].Value;
                string repository = matcher.Groups[2].Value;
                string tag = matcher.Groups[3].Value;
                string digest = matcher.Groups[4].Value;

                if (string.IsNullOrEmpty(registry))
                {
                    registry = DOCKER_HUB_REGISTRY;
                }

                if (string.IsNullOrEmpty(repository))
                {
                    throw new Exception(reference);
                }

                if (!registry.Contains(".") && !registry.Contains(":") && "localhost" != registry)
                {
                    repository = registry + "/" + repository;
                    registry = DOCKER_HUB_REGISTRY;
                }

                if (DOCKER_HUB_REGISTRY == registry && repository.IndexOf('/') < 0)
                {
                    repository = LIBRARY_REPOSITORY_PREFIX + repository;
                }

                if (!string.IsNullOrEmpty(tag))
                {
                    if (!string.IsNullOrEmpty(digest))
                    {
                        throw new Exception(reference);
                    }
                }
                else if (!string.IsNullOrEmpty(digest))
                {
                    tag = digest;
                }
                else
                {
                    tag = DEFAULT_TAG;
                }

                return new ImageReference(registry, repository, tag);
            }
            catch (Exception e)
            {
                return null;
            }
        }

        public static ImageReference Of(string registry, string repository, string tag)
        {
            if (!string.IsNullOrEmpty(registry) && !IsValidRegistry(registry))
            {
                throw new ArgumentException($"'{registry}' is not a valid registry", nameof(registry));
            }
            if (!IsValidRepository(repository))
            {
                throw new ArgumentException($"'{repository}' is not a valid repository", nameof(repository));
            }
            if (!string.IsNullOrEmpty(tag) && !IsValidTag(tag))
            {
                throw new ArgumentException($"'{tag}' is not a valid tag", nameof(tag));
            }

            if (string.IsNullOrEmpty(registry))
            {
                registry = DOCKER_HUB_REGISTRY;
            }
            if (string.IsNullOrEmpty(tag))
            {
                tag = DEFAULT_TAG;
            }
            return new ImageReference(registry, repository, tag);
        }

        public static ImageReference Scratch()
        {
            return new ImageReference("", "scratch", "");
        }

        public static bool IsValidRegistry(string registry)
        {
            var match = Regex.Match(registry, REFERENCE_REGEX);
            return match.Success && match.Value == registry;
        }

        public static bool IsValidRepository(string repository)
        {
            var match = Regex.Match(repository, REPOSITORY_REGEX);
            return match.Success && match.Value == repository;
        }

        public static bool IsValidTag(string tag)
        {
            return IsValidTagName(tag) || DescriptorDigest.IsValidDigest(tag);
        }

        private static bool IsValidTagName(string tag)
        {
            var match = Regex.Match(tag, TAG_REGEX);
            return match.Success && match.Value == tag;
        }

        public static bool IsDefaultTag(string tag)
        {
            return string.IsNullOrEmpty(tag) || DEFAULT_TAG == tag;
        }

        private readonly string registry;
        private readonly string repository;
        private readonly string tag;

        private ImageReference(string registry, string repository, string tag)
        {
            this.registry = RegistryAliasGroup.GetHost(registry);
            this.repository = repository;
            this.tag = tag;
        }

        public string GetRegistry()
        {
            return registry;
        }

        public string GetRepository()
        {
            return repository;
        }

        public string GetTag()
        {
            return tag;
        }

        public bool UsesDefaultTag()
        {
            return IsDefaultTag(tag);
        }

        public bool IsTagDigest()
        {
            return DescriptorDigest.IsValidDigest(tag);
        }

        public bool IsScratch()
        {
            return string.IsNullOrEmpty(registry) && "scratch" == repository && string.IsNullOrEmpty(tag);
        }

        public ImageReference WithTag(string newTag)
        {
            return Of(registry, repository, newTag);
        }

        public override string ToString()
        {
            StringBuilder referenceString = new StringBuilder();

            if (DOCKER_HUB_REGISTRY != registry)
            {
                referenceString.Append(registry).Append('/').Append(repository);
            }
            else if (repository.StartsWith(LIBRARY_REPOSITORY_PREFIX, StringComparison.Ordinal))
            {
                string repositorySubstring = repository.Substring(LIBRARY_REPOSITORY_PREFIX.Length);
                referenceString.Append(repositorySubstring);
            }
            else
            {
                referenceString.Append(repository);
            }

            if (DEFAULT_TAG != tag)
            {
                referenceString.Append(IsTagDigest() ? '@' : ':').Append(tag);
            }

            return referenceString.ToString();
        }

        public string ToStringWithTag()
        {
            return ToString() + (UsesDefaultTag() ? ":" + DEFAULT_TAG : "");
        }
    }

    public sealed class DescriptorDigest
    {
        public static readonly int HashLength = 64;

        private static readonly string HASH_REGEX = $"[a-f0-9]{{{HashLength}}}";

        private const string DIGEST_PREFIX = "sha256:";

        public static readonly string DigestRegex = DIGEST_PREFIX + HASH_REGEX;

        private readonly string hash;

        public static DescriptorDigest FromHash(string hash)
        {
            var match = Regex.Match(hash, HASH_REGEX);
            if (!match.Success || match.Value != hash)
            {
                throw new Exception("Invalid hash: " + hash);
            }

            return new DescriptorDigest(hash);
        }

        public static DescriptorDigest FromDigest(string digest)
        {
            digest = digest ?? throw new ArgumentNullException(nameof(digest));
            if (!IsValidDigest(digest))
            {
                throw new Exception("Invalid digest: " + digest);
            }

            string hash = digest.Substring(DIGEST_PREFIX.Length);
            return new DescriptorDigest(hash);
        }

        public static bool IsValidDigest(string digest)
        {
            var match = Regex.Match(digest, DigestRegex);
            return match.Success && match.Value == digest;
        }

        private DescriptorDigest(string hash)
        {
            this.hash = hash;
        }

        public string GetHash()
        {
            return hash;
        }

        public override string ToString()
        {
            return "sha256:" + hash;
        }

        public override int GetHashCode()
        {
            return hash.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            if (obj is DescriptorDigest descriptorDigest)
            {
                return hash == descriptorDigest.hash;
            }

            return false;
        }
    }

    public static class RegistryAliasGroup
    {
        private static readonly List<string> REGISTRY_ALIAS_GROUPS = new List<string>()
        {
            "registry.hub.docker.com", "index.docker.io", "registry-1.docker.io", "docker.io"
        };

        private static readonly Dictionary<string, string> REGISTRY_HOST_MAP =
            new Dictionary<string, string>
            {
                ["docker.io"] = "registry-1.docker.io"
            };

        public static string GetHost(string registry)
        {
            if (REGISTRY_HOST_MAP.TryGetValue(registry, out var host))
            {
                return host;
            }
            else
            {
                return registry;
            }
        }
    }
}
