import Script from "next/script";
import { FaqSection } from "@/components/landing/FaqSection";
import { FinalCta } from "@/components/landing/FinalCta";
import { HeroSection } from "@/components/landing/HeroSection";
import { HiveEcosystemSection } from "@/components/landing/HiveEcosystemSection";
import { HowItWorks } from "@/components/landing/HowItWorks";
import { PricingPreview } from "@/components/landing/PricingPreview";
import { TrustSection } from "@/components/landing/TrustSection";
import { ValueGrid } from "@/components/landing/ValueGrid";
import { siteConfig } from "@/lib/site";

export default function Home() {
  const softwareSchema = {
    "@context": "https://schema.org",
    "@type": "SoftwareApplication",
    name: "HiveOps",
    brand: {
      "@type": "Brand",
      name: "HIVE",
    },
    applicationCategory: "BusinessApplication",
    operatingSystem: "Web",
    url: siteConfig.url,
    description: siteConfig.description,
    offers: {
      "@type": "Offer",
      url: `${siteConfig.url}/contact`,
      description:
        "Modelo por resultado: se cotiza por incidente resuelto y verificado. Solicitá una propuesta a medida.",
    },
  };

  return (
    <div className="pb-8">
      <Script
        id="software-schema"
        type="application/ld+json"
        dangerouslySetInnerHTML={{ __html: JSON.stringify(softwareSchema) }}
      />
      <HeroSection />
      <ValueGrid />
      <HowItWorks />
      <HiveEcosystemSection />
      <PricingPreview />
      <TrustSection />
      <FaqSection />
      <FinalCta />
    </div>
  );
}
