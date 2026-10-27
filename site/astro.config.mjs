import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
  site: 'https://orbitmpgh.github.io',
  base: '/Maki',
  integrations: [
    starlight({
      title: 'Maki',
      description: 'Documentation for Maki, the self-hosted manga manager with a reader built in.',
      logo: { src: './public/favicon.svg' },
      favicon: '/favicon.svg',
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/OrbitMPGH/Maki' }],
      editLink: { baseUrl: 'https://github.com/OrbitMPGH/Maki/edit/main/site/' },
      customCss: ['./src/styles/maki.css'],
      lastUpdated: true,
      sidebar: [
        {
          label: 'Introduction',
          collapsed: true,
          items: [
            'introduction/what-is-maki',
            'introduction/how-it-works',
          ],
        },
        {
          label: 'Features',
          collapsed: true,
          items: [
            'features',
            'features/reader',
            'features/reading-apps',
            'features/trackers',
            'features/monitoring-and-downloads',
            'features/files-and-library',
            'features/discover',
            'features/page-enhance',
            'features/stats',
            'features/administration',
          ],
        },
        {
          label: 'Installation',
          collapsed: true,
          items: [
            'installation/docker-compose',
            'installation/unraid',
            'installation/reverse-proxy-https',
            'installation/single-sign-on',
          ],
        },
        {
          label: 'Guides',
          collapsed: true,
          items: [
            {
              label: 'Getting started',
              collapsed: true,
              items: ['guides/getting-started/add-your-first-series'],
            },
            {
              label: 'Reading in other apps',
              collapsed: true,
              items: [
                'guides/reading/offline-and-app',
                'guides/reading/koreader',
                'guides/reading/opds-apps',
              ],
            },
            {
              label: 'Sources',
              collapsed: true,
              items: ['guides/sources/built-in-sources'],
            },
            {
              label: 'Recommendations',
              collapsed: true,
              items: ['guides/recommendations/how-it-works'],
            },
            {
              label: 'Running Maki',
              collapsed: true,
              items: ['guides/running/users-and-access', 'guides/running/backup-restore'],
            },
          ],
        },
        {
          label: 'Contribution',
          collapsed: true,
          items: ['contribution/development', 'contribution/translating'],
        },
        { label: 'Q&A', collapsed: true, items: ['qa/faq'] },
      ],
    }),
  ],
});
