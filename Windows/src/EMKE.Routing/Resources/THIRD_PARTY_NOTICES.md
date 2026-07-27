# Third-party notices

## Offline language profile v1

The embedded zh/en/de language profile is generated from the following Project
Gutenberg UTF-8 sources. The source files are not included in the application
or repository. Project Gutenberg states that these works are public domain in
the United States and permits reuse under the Project Gutenberg License; users
outside the United States must check local copyright law.

| Language | Source | License | Corpus SHA256 |
| --- | --- | --- | --- |
| zh | [紅樓夢](https://www.gutenberg.org/ebooks/24264.txt.utf-8) | [Project Gutenberg License](https://www.gutenberg.org/policy/license.html) | `ff1526996bf4b81807651921a85e5c1c0f1d1d123c9fa4553057ba6a3ec72011` |
| en | [Pride and Prejudice](https://www.gutenberg.org/ebooks/1342.txt.utf-8) | [Project Gutenberg License](https://www.gutenberg.org/policy/license.html) | `74f2665d6e6925fc2c17dec644bec9e87df478a0f1836822125e8acbb3777806` |
| de | [Faust: Der Tragödie erster Teil](https://www.gutenberg.org/ebooks/2229.txt.utf-8) | [Project Gutenberg License](https://www.gutenberg.org/policy/license.html) | `fed2a58edd6910ee96d27ada614d34bcc41f03750d86541c41246bb74c564ff7` |

- Generator: `emke-language-profile/1.0.0`
- Feature data SHA256: `ed4af3c409b94baae48d4b10b277edf61fb6e944a9a2029cbfa68b44cfd45f84`
- Generated model SHA256: `b6373c71cc5104fb4d2f708d2a487b15fdd3bc9b63c49e56c7133dbcfb809e46`
- Reproduction: run `node Windows/tools/generate-language-profile.mjs`. The
  generator rejects downloaded source bytes unless every corpus SHA256 matches.
