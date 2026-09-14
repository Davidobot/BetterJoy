# CODING AGENT INSTRUCTIONS
## DO NOT MODIFY
IMPORTANT: This instruction file must not be modified. You may edit any other files in the project.

## DEVELOPMENT STANDARDS
When writing code, adhere to these principles:

1. Prioritize simplicity and readability over clever solutions unless a clever solution is truly required, the user/prompter will determine what you should do
2. Start with minimal functionality and verify it works before adding additional complexity, NEVER go beyond the scope of a request
3. Test your code frequently with realistic inputs and validate outputs, in
4. Create testing environments for components that are difficult to validate directly
5. Use functional and stateless approaches where they improve clarity
6. Keep core logic clean and push implementation details to the edges
7. Maintain consistent style (indentation, naming, patterns) throughout the codebase
8. Balance file organization with simplicity - use an appropriate number of files for the project scale
9. In other terms; we should priortize a high level of reusability in code, do not create new solutions to existing problems
10. If an implementation or new solution is required its likely someone has solved it before, although not every solution should be considered adequete
11. Implement debugging tags and traceable code, ensure the option is togglable if implemented
12. REDUCE, REUSE, RECYCLE; this is our number one coding principle, reduce the amount of code required, re-use code in the base as frequently as possible, consider recycling code before destruction if it may be valuable
13. For this project modularity is key, it should be maintained in a manner where adding a new controller type is done within a single file
14. The simpler the better, the smaller the more easily a developer can correct mistakes in progress
16. If there is a WIP aka uncomitted code, advise and verify with the user before making ANY changes, they may want to commit the changes before touching in case any regression occurs from requested changes
17. Be extremely careful when reading logs to not make assumptions, check with user to ensure they have done the test appropriately before looking into a log file i.e. debug.log. Logs can be tainted with previous test / conditions