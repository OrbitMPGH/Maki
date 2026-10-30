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
      customCss: [
        '@fontsource/onest/400.css',
        '@fontsource/onest/500.css',
        '@fontsource/onest/600.css',
        '@fontsource/onest/700.css',
        '@fontsource/jetbrains-mono/400.css',
        './src/styles/maki.css',
      ],
      lastUpdated: true,
      sidebar: [
        {
          label: 'Introduction',
          collapsed: true,
          items: [
            'introduction/what-is-maki',
            'introduction/how-it-works',
            'introduction/legal',
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
            {
              label: 'Docker',
              collapsed: true,
              items: [
                'installation/docker-compose',
                'installation/companions',
                'installation/volumes-and-permissions',
              ],
            },
            {
              label: 'Unraid',
              collapsed: true,
              items: [
                'installation/unraid',
                'installation/unraid-paths',
              ],
            },
            {
              label: 'Windows',
              collapsed: true,
              items: [
                'installation/windows',
              ],
            },
            {
              label: 'Linux',
              collapsed: true,
              items: [
                'installation/linux',
              ],
            },
            {
              label: 'From source',
              collapsed: true,
              items: [
                'installation/from-source',
              ],
            },
            {
              label: 'After installing',
              collapsed: true,
              items: [
                'installation/first-run',
                'installation/upgrading',
                'installation/reverse-proxy-https',
                'installation/access-log-crowdsec',
                'installation/single-sign-on',
              ],
            },
          ],
        },
        {
          label: 'Guides',
          collapsed: true,
          items: [
            {
              label: 'Getting started',
              collapsed: true,
              items: [
                'guides/getting-started/add-your-first-series',
                'guides/getting-started/activity-and-queue',
                'guides/getting-started/import-existing-library',
                'guides/getting-started/import-mihon-backup',
              ],
            },
            {
              label: 'Reading in other apps',
              collapsed: true,
              items: [
                'guides/reading/maki-reader',
                'guides/reading/offline-and-app',
                'guides/reading/opds-apps',
                'guides/reading/koreader',
                'guides/reading/kavita',
                'guides/reading/komga-apps',
                'guides/reading/progress-sync',
              ],
            },
            {
              label: 'Sources and downloads',
              collapsed: true,
              items: [
                'guides/sources/built-in-sources',
                'guides/sources/choosing-sources',
                'guides/sources/flaresolverr',
                'guides/sources/suwayomi',
                'guides/sources/torrents',
                'guides/sources/custom-sources',
                'guides/sources/mirror-overrides',
              ],
            },
            {
              label: 'Library and files',
              collapsed: true,
              items: [
                'guides/library/folder-layout',
                'guides/library/cbz-and-comicinfo',
                'guides/library/chapter-upgrades',
                'guides/library/page-conversion',
                'guides/library/page-enhance',
                'guides/library/archiving',
                'guides/library/retention',
              ],
            },
            {
              label: 'Trackers',
              collapsed: true,
              items: [
                'guides/trackers/connect',
                'guides/trackers/what-counts-as-read',
                'guides/trackers/ratings',
              ],
            },
            {
              label: 'Recommendations',
              collapsed: true,
              items: [
                'guides/recommendations/how-it-works',
                'guides/recommendations/rails',
                'guides/recommendations/local-database',
                'guides/recommendations/feedback',
                'guides/recommendations/follow-creators',
                'guides/recommendations/custom-rails',
              ],
            },
            {
              label: 'Running Maki',
              collapsed: true,
              items: [
                'guides/running/users-and-access',
                'guides/running/two-factor-and-api-keys',
                'guides/running/notifications',
                'guides/running/backup-restore',
                'guides/running/stats-and-rewind',
                'guides/running/health-checks',
              ],
            },
          ],
        },
        {
          label: 'Reference',
          collapsed: true,
          items: [
            'reference/settings',
            'reference/environment-variables',
            'reference/api',
            'reference/ports-and-paths',
          ],
        },
        {
          label: 'Contribution',
          collapsed: true,
          items: [
            'contribution/ways-to-help',
            'contribution/translating',
            'contribution/development',
            'contribution/architecture',
            'contribution/add-a-source',
            'contribution/tests-and-ci',
            'contribution/pull-requests',
            'contribution/writing-docs',
          ],
        },
        {
          label: 'Q&A',
          collapsed: true,
          items: [
            'qa/faq',
            'qa/install',
            'qa/sources',
            'qa/reading',
            'qa/recommendations',
            'qa/accounts',
            'qa/troubleshooting',
          ],
        },
      ],
    }),
  ],
});
