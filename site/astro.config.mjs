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
          items: [
            'introduction/what-is-maki',
            'introduction/how-it-works',
            'introduction/features',
          ],
        },
        {
          label: 'Installation',
          items: [
            'installation/docker-compose',
            'installation/unraid',
            'installation/reverse-proxy-https',
            'installation/single-sign-on',
          ],
        },
        {
          label: 'Guides',
          items: [
            {
              label: 'Getting started',
              items: ['guides/getting-started/add-your-first-series'],
            },
            {
              label: 'Reading in other apps',
              items: [
                'guides/reading/offline-and-app',
                'guides/reading/koreader',
                'guides/reading/opds-apps',
              ],
            },
            {
              label: 'Sources',
              items: ['guides/sources/built-in-sources'],
            },
            {
              label: 'Recommendations',
              items: ['guides/recommendations/how-it-works'],
            },
            {
              label: 'Running Maki',
              items: ['guides/running/users-and-access', 'guides/running/backup-restore'],
            },
          ],
        },
        {
          label: 'Contribution',
          items: ['contribution/development', 'contribution/translating'],
        },
        { label: 'Q&A', items: ['qa/faq'] },
      ],
    }),
  ],
});
