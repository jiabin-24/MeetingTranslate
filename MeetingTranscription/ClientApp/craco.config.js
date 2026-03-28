const path = require('path');

module.exports = {
  webpack: {
    configure: (webpackConfig, { env, paths }) => {
      webpackConfig.optimization = webpackConfig.optimization || {};
      webpackConfig.optimization.splitChunks = {
        chunks: 'all',
        cacheGroups: {
          reactVendor: {
            test: /[\\/]node_modules[\\/](react|react-dom|react-router-dom)[\\/]/,
            name: 'react-vendors',
            chunks: 'all',
            priority: 30,
            enforce: true,
          },
          teamsSdk: {
            test: /[\\/]node_modules[\\/]@microsoft[\\/]teams-js[\\/]/,
            name: 'teams-sdk',
            chunks: 'all',
            priority: 25,
            enforce: true,
          },
          azureSdk: {
            test: /[\\/]node_modules[\\/]@azure[\\/]/,
            name: 'azure-sdk',
            chunks: 'all',
            priority: 20,
            enforce: true,
          },
          defaultVendor: {
            test: /[\\/]node_modules[\\/]/,
            name: 'vendor',
            chunks: 'all',
            priority: 10,
            reuseExistingChunk: true,
          },
          default: {
            minChunks: 2,
            priority: -20,
            reuseExistingChunk: true,
          },
        },
      };
      return webpackConfig;
    },
  },
};
