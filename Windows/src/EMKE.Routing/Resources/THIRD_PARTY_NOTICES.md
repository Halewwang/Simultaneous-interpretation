# Third-party notices

## Offline language profile v1

The embedded zh/en/de language profile is generated from the following Project
Gutenberg UTF-8 sources. The source files are not included in the application
or repository. Project Gutenberg states that these works are public domain in
the United States and permits reuse under the Project Gutenberg License; users
outside the United States must check local copyright law.

| Language | Source | License | Corpus SHA256 |
| --- | --- | --- | --- |
| zh | [紅樓夢](https://www.gutenberg.org/cache/epub/24264/pg24264.txt) | [Project Gutenberg License](https://www.gutenberg.org/policy/license.html) | `ff1526996bf4b81807651921a85e5c1c0f1d1d123c9fa4553057ba6a3ec72011` |
| en | [Pride and Prejudice](https://www.gutenberg.org/cache/epub/1342/pg1342.txt) | [Project Gutenberg License](https://www.gutenberg.org/policy/license.html) | `74f2665d6e6925fc2c17dec644bec9e87df478a0f1836822125e8acbb3777806` |
| de | [Faust: Der Tragödie erster Teil](https://www.gutenberg.org/cache/epub/2229/pg2229.txt) | [Project Gutenberg License](https://www.gutenberg.org/policy/license.html) | `fed2a58edd6910ee96d27ada614d34bcc41f03750d86541c41246bb74c564ff7` |

- Generator: `emke-language-profile/1.1.0`
- Feature data SHA256: `a64f8e589f873628197ebcb3efbc5c09b031d3190626697c49d12be86ca7a603`
- Generated model SHA256: `1ba37bc5dc78b1da24a525a9861a53923b8aff8821394851ef2ab36e77b1e7f8`
- Reproduction: run `node Windows/tools/generate-language-profile.mjs`. The
  generator rejects downloaded source bytes unless every corpus SHA256 matches.
