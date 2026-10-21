import type { MetadataRoute } from "next";
import { siteConfig } from "@/lib/site";

export default function sitemap(): MetadataRoute.Sitemap {
  return [
    { url: `${siteConfig.url}/`, priority: 1, changeFrequency: "weekly" },
    { url: `${siteConfig.url}/pricing`, priority: 0.9, changeFrequency: "weekly" },
    { url: `${siteConfig.url}/security`, priority: 0.7, changeFrequency: "monthly" },
    { url: `${siteConfig.url}/contact`, priority: 0.7, changeFrequency: "monthly" },
  ];
}
