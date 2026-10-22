import type { StorybookConfig } from '@storybook/react-vite';
import type { Plugin } from 'vite';

const IMAGE_ROUTE = /^\/(mock\/|api\/v1\/mediacover\/|api\/v1\/signin\/mosaic\.webp|api\/v1\/reader\/chapter\/[^/]+\/(page|thumb)\/)/;

// msw's Service Worker answers <img> requests in a normal browser. Where none can register,
// preview.tsx mocks by patching fetch and XHR instead, which images never use, so they reach this
// server and get the same generated SVGs here.
function mockImages(): Plugin {
  return {
    name: 'maki-mock-images',
    configureServer(server) {
      server.middlewares.use(async (req, res, next) => {
        if (req.method !== 'GET' || !req.url || !IMAGE_ROUTE.test(req.url)) return next();
        try {
          const origin = `http://${req.headers.host ?? 'localhost'}`;
          const [{ getResponse }, { imageHandlers }] = await Promise.all([
            import('msw'),
            server.ssrLoadModule('/.storybook/mocks/images.ts'),
          ]);
          const response = await getResponse(imageHandlers(), new Request(new URL(req.url, origin)), { baseUrl: origin });
          if (!response) return next();
          res.statusCode = response.status;
          response.headers.forEach((value, key) => res.setHeader(key, value));
          res.end(Buffer.from(await response.arrayBuffer()));
        } catch (error) {
          next(error);
        }
      });
    },
  };
}

const config: StorybookConfig = {
  "stories": [
    "../src/**/*.mdx",
    "../src/**/*.stories.@(js|jsx|mjs|ts|tsx)"
  ],
  "addons": [
    "@chromatic-com/storybook",
    "@storybook/addon-vitest",
    "@storybook/addon-a11y",
    "@storybook/addon-docs",
    "@storybook/addon-mcp",
    "msw-storybook-addon"
  ],
  "framework": "@storybook/react-vite",
  viteFinal: (config) => ({ ...config, plugins: [...(config.plugins ?? []), mockImages()] })
};
export default config;
